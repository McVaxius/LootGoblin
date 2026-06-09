using System;
using System.Collections.Generic;
using System.Text;

namespace LootGoblin.Services;

internal sealed record DiagnosticSnapshot(
    DateTime CapturedAtUtc,
    string Source,
    IReadOnlyList<KeyValuePair<string, string>> Fields)
{
    public string Format()
    {
        var builder = new StringBuilder();
        builder.Append("source=").Append(Quote(Source));
        builder.Append("; capturedUtc=").Append(CapturedAtUtc.ToString("O"));

        foreach (var field in Fields)
        {
            builder.Append("; ")
                .Append(field.Key)
                .Append('=')
                .Append(Quote(field.Value));
        }

        return builder.ToString();
    }

    private static string Quote(string? value)
    {
        var escaped = (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
