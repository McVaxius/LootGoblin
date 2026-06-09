using System;
using System.IO;
using System.Linq;
using LootGoblin.Services;
using Xunit;

namespace LootGoblin.Tests;

public sealed class DedicatedDiagnosticLogTests
{
    [Fact]
    public void DisabledLogCreatesNoFiles()
    {
        using var temp = new TemporaryDirectory();
        using var log = new DedicatedDiagnosticLog(temp.Path);

        log.Write("EVENT", "should not be written");
        log.Flush();

        Assert.False(Directory.Exists(temp.Path));
    }

    [Fact]
    public void EnableWritesAndDisableFlushes()
    {
        using var temp = new TemporaryDirectory();
        using var log = new DedicatedDiagnosticLog(temp.Path);

        log.Enable();
        log.Write("SNAPSHOT", "initial=true");
        log.Disable("test");

        var text = ReadAllLogs(temp.Path);
        Assert.Contains("Dedicated diagnostic log enabled.", text);
        Assert.Contains("initial=true", text);
        Assert.Contains("Dedicated diagnostic log disabling: test", text);
    }

    [Fact]
    public void RotatesAndRetainsNewestFiles()
    {
        using var temp = new TemporaryDirectory();
        using var log = new DedicatedDiagnosticLog(temp.Path, maxFileBytes: 220, maxFiles: 3);

        log.Enable();
        for (var i = 0; i < 30; i++)
            log.Write("EVENT", $"unique-{i:D2}-{new string('x', 80)}");
        log.Disable("rotation-test");

        var files = Directory.GetFiles(temp.Path, "LootGoblin-diagnostic-*.log");
        Assert.InRange(files.Length, 2, 3);
        Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 220));
        Assert.Contains("unique-29", ReadAllLogs(temp.Path));
    }

    [Fact]
    public void RepeatedMessagesProduceSuppressionSummary()
    {
        using var temp = new TemporaryDirectory();
        using var log = new DedicatedDiagnosticLog(temp.Path);

        log.Enable();
        for (var i = 0; i < 5; i++)
            log.Write("EVENT", "[AutoDiscard] Deferred: loading.");
        log.Write("EVENT", "different");
        log.Disable("suppression-test");

        var text = ReadAllLogs(temp.Path);
        Assert.Single(
            text.Split(Environment.NewLine),
            line => line.Split(' ', 3) is { Length: 3 } parts &&
                    parts[1] == "[EVENT]" &&
                    parts[2] == "[AutoDiscard] Deferred: loading.");
        Assert.Contains("[SUPPRESSED] count=4", text);
    }

    private static string ReadAllLogs(string path)
        => string.Join(
            Environment.NewLine,
            Directory.GetFiles(path, "LootGoblin-diagnostic-*.log")
                .OrderBy(file => file, StringComparer.Ordinal)
                .Select(File.ReadAllText));

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"LootGoblin.Tests-{Guid.NewGuid():N}");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
