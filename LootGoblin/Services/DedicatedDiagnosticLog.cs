using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LootGoblin.Services;

internal sealed class DedicatedDiagnosticLog : IDisposable
{
    internal const long DefaultMaxFileBytes = 20L * 1024L * 1024L;
    internal const int DefaultMaxFiles = 10;
    internal const int DefaultBufferCapacity = 2048;

    private const string FilePrefix = "LootGoblin-diagnostic-";
    private const string FilePattern = FilePrefix + "*.log";

    private readonly object lifecycleLock = new();
    private readonly string directoryPath;
    private readonly long maxFileBytes;
    private readonly int maxFiles;
    private readonly int bufferCapacity;

    private BlockingCollection<LogRequest>? queue;
    private Task? writerTask;
    private bool enabled;
    private long droppedMessages;
    private long currentFileBytes;

    public DedicatedDiagnosticLog(
        string directoryPath,
        long maxFileBytes = DefaultMaxFileBytes,
        int maxFiles = DefaultMaxFiles,
        int bufferCapacity = DefaultBufferCapacity)
    {
        this.directoryPath = directoryPath;
        this.maxFileBytes = Math.Max(1, maxFileBytes);
        this.maxFiles = Math.Max(1, maxFiles);
        this.bufferCapacity = Math.Max(1, bufferCapacity);
    }

    public string DirectoryPath => directoryPath;

    public bool IsEnabled
    {
        get
        {
            lock (lifecycleLock)
                return enabled;
        }
    }

    public void Enable()
    {
        lock (lifecycleLock)
        {
            if (enabled)
                return;

            Directory.CreateDirectory(directoryPath);
            Interlocked.Exchange(ref droppedMessages, 0);
            currentFileBytes = 0;
            queue = new BlockingCollection<LogRequest>(bufferCapacity);
            enabled = true;
            writerTask = Task.Run(() => RunWriter(queue));
        }

        Write("LIFECYCLE", "Dedicated diagnostic log enabled.");
    }

    public void Write(string category, string message)
        => EnqueueLine(category, message, waitForBuffer: false);

    public void WriteCritical(string category, string message)
        => EnqueueLine(category, message, waitForBuffer: true);

    private void EnqueueLine(string category, string message, bool waitForBuffer)
    {
        BlockingCollection<LogRequest>? activeQueue;
        lock (lifecycleLock)
        {
            if (!enabled)
                return;

            activeQueue = queue;
        }

        if (activeQueue == null)
            return;

        var cleanCategory = string.IsNullOrWhiteSpace(category) ? "EVENT" : category.Trim();
        var cleanMessage = Sanitize(message);
        var line = $"{DateTime.UtcNow:O} [{cleanCategory}] {cleanMessage}";
        var collapseKey = BuildCollapseKey(cleanCategory, cleanMessage);
        try
        {
            var request = LogRequest.Line(line, collapseKey);
            var added = waitForBuffer
                ? activeQueue.TryAdd(request, millisecondsTimeout: 1000)
                : activeQueue.TryAdd(request);
            if (!added)
                Interlocked.Increment(ref droppedMessages);
        }
        catch (InvalidOperationException)
        {
            // Writer is already shutting down.
        }
    }

    public void Flush()
    {
        BlockingCollection<LogRequest>? activeQueue;
        lock (lifecycleLock)
        {
            if (!enabled)
                return;

            activeQueue = queue;
        }

        if (activeQueue == null)
            return;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            activeQueue.Add(LogRequest.Flush(completion));
            WaitForRequestOrWriterExit(completion.Task);
        }
        catch (InvalidOperationException)
        {
            // Writer is already shutting down.
        }
    }

    public void Disable(string reason)
    {
        BlockingCollection<LogRequest>? activeQueue;
        Task? activeWriter;
        lock (lifecycleLock)
        {
            if (!enabled)
                return;

            activeQueue = queue;
            activeWriter = writerTask;
            enabled = false;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var cleanReason = Sanitize(reason);
            var line = $"{DateTime.UtcNow:O} [LIFECYCLE] Dedicated diagnostic log disabling: {cleanReason}";
            activeQueue?.Add(LogRequest.Line(line, BuildCollapseKey("LIFECYCLE", line)));
            activeQueue?.Add(LogRequest.Stop(completion));
            activeQueue?.CompleteAdding();
            WaitForRequestOrWriterExit(completion.Task, activeWriter);
            activeWriter?.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Writer is already shutting down.
        }
        finally
        {
            lock (lifecycleLock)
            {
                queue?.Dispose();
                queue = null;
                writerTask = null;
            }
        }
    }

    public void Dispose()
        => Disable("dispose");

    private void RunWriter(BlockingCollection<LogRequest> activeQueue)
    {
        StreamWriter? writer = null;
        string? lastCollapseKey = null;
        string? lastLine = null;
        var suppressedCount = 0;

        try
        {
            writer = OpenNewWriter();
            foreach (var request in activeQueue.GetConsumingEnumerable())
            {
                WriteDroppedSummaryIfNeeded(ref writer);

                if (request.Kind == LogRequestKind.Line)
                {
                    if (string.Equals(lastCollapseKey, request.CollapseKey, StringComparison.Ordinal))
                    {
                        suppressedCount++;
                        continue;
                    }

                    WriteSuppressionSummary(ref writer, lastLine, suppressedCount);
                    suppressedCount = 0;
                    WriteLine(ref writer, request.Text);
                    lastCollapseKey = request.CollapseKey;
                    lastLine = request.Text;
                    continue;
                }

                WriteSuppressionSummary(ref writer, lastLine, suppressedCount);
                suppressedCount = 0;
                lastCollapseKey = null;
                lastLine = null;
                writer.Flush();
                request.Completion?.TrySetResult();

                if (request.Kind == LogRequestKind.Stop)
                    break;
            }
        }
        catch (Exception ex)
        {
            while (activeQueue.TryTake(out var request))
                request.Completion?.TrySetException(ex);
        }
        finally
        {
            writer?.Dispose();
        }
    }

    private StreamWriter OpenNewWriter()
    {
        Directory.CreateDirectory(directoryPath);
        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff");
        var path = Path.Combine(directoryPath, $"{FilePrefix}{timestamp}.log");
        var suffix = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(directoryPath, $"{FilePrefix}{timestamp}-{suffix}.log");
            suffix++;
        }

        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
        var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = false,
        };
        currentFileBytes = 0;
        RetainNewestFiles(path);
        return writer;
    }

    private void WriteLine(ref StreamWriter writer, string line)
    {
        var requiredBytes = Encoding.UTF8.GetByteCount(line + Environment.NewLine);
        if (currentFileBytes > 0 && currentFileBytes + requiredBytes > maxFileBytes)
        {
            writer.Flush();
            writer.Dispose();
            writer = OpenNewWriter();
        }

        writer.WriteLine(line);
        currentFileBytes += requiredBytes;
    }

    private void WriteSuppressionSummary(ref StreamWriter writer, string? repeatedLine, int suppressedCount)
    {
        if (suppressedCount <= 0 || string.IsNullOrWhiteSpace(repeatedLine))
            return;

        WriteLine(
            ref writer,
            $"{DateTime.UtcNow:O} [SUPPRESSED] count={suppressedCount}; repeated={Sanitize(repeatedLine)}");
    }

    private void WriteDroppedSummaryIfNeeded(ref StreamWriter writer)
    {
        var dropped = Interlocked.Exchange(ref droppedMessages, 0);
        if (dropped <= 0)
            return;

        WriteLine(ref writer, $"{DateTime.UtcNow:O} [BUFFER] dropped={dropped}; reason=bounded-buffer-full");
    }

    private void RetainNewestFiles(string currentPath)
    {
        var currentFullPath = Path.GetFullPath(currentPath);
        var files = new DirectoryInfo(directoryPath)
            .GetFiles(FilePattern, SearchOption.TopDirectoryOnly)
            .Where(file => !string.Equals(file.FullName, currentFullPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(file => file.CreationTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.Ordinal)
            .ToList();

        foreach (var file in files.Skip(maxFiles - 1))
        {
            try
            {
                file.Delete();
            }
            catch
            {
                // Retention failure must not stop diagnostic logging.
            }
        }
    }

    private void WaitForRequestOrWriterExit(Task requestTask, Task? activeWriter = null)
    {
        activeWriter ??= writerTask;
        if (activeWriter == null)
            return;

        Task.WhenAny(requestTask, activeWriter).GetAwaiter().GetResult();
        if (requestTask.IsCompletedSuccessfully)
            return;

        if (!activeWriter.IsCompleted)
            activeWriter.GetAwaiter().GetResult();
    }

    private static string BuildCollapseKey(string category, string message)
    {
        if (message.StartsWith("[AutoDiscard] Deferred:", StringComparison.Ordinal))
            return $"{category}|[AutoDiscard] Deferred:{message["[AutoDiscard] Deferred:".Length..]}";

        if (message.StartsWith("Food resolved ", StringComparison.Ordinal) ||
            message.StartsWith("Food search found:", StringComparison.Ordinal))
        {
            return $"{category}|{message}";
        }

        if (message.StartsWith("Inventory scan", StringComparison.Ordinal) ||
            message.StartsWith("[Inventory]", StringComparison.Ordinal))
        {
            return $"{category}|{message}";
        }

        return $"{category}|{message}";
    }

    private static string Sanitize(string? value)
        => (value ?? string.Empty)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private enum LogRequestKind
    {
        Line,
        Flush,
        Stop,
    }

    private sealed record LogRequest(
        LogRequestKind Kind,
        string Text,
        string CollapseKey,
        TaskCompletionSource? Completion)
    {
        public static LogRequest Line(string text, string collapseKey)
            => new(LogRequestKind.Line, text, collapseKey, null);

        public static LogRequest Flush(TaskCompletionSource completion)
            => new(LogRequestKind.Flush, string.Empty, string.Empty, completion);

        public static LogRequest Stop(TaskCompletionSource completion)
            => new(LogRequestKind.Stop, string.Empty, string.Empty, completion);
    }
}
