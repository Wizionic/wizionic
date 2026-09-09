using System.Globalization;
using System.Text.RegularExpressions;

namespace App.Core.Setup;

/// <summary>Best-effort parse of lemonade/ollama/huggingface pull stdout for a wizard progress bar.</summary>
public static class PullProgressParser
{
    private static readonly Regex Percent = new(@"(\d{1,3})\s*%", RegexOptions.Compiled);
    private static readonly Regex Pair = new(
        @"([\d.,]+)\s*(KiB|KB|MiB|MB|GiB|GB)\s*/\s*([\d.,]+)\s*(KiB|KB|MiB|MB|GiB|GB)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static int? TryPercent(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        var m = Percent.Match(line);
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var p))
            return null;
        return Math.Clamp(p, 0, 100);
    }

    public static string? TryByteSummary(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;
        var m = Pair.Match(line);
        if (!m.Success)
            return null;
        return $"{m.Groups[1].Value} {m.Groups[2].Value} / {m.Groups[3].Value} {m.Groups[4].Value}";
    }

    public static string TruncateLine(string? line, int max = 96)
    {
        if (string.IsNullOrWhiteSpace(line))
            return "";
        var t = line.Trim();
        t = t.Replace('\r', ' ').Replace('\n', ' ');
        return t.Length <= max ? t : t[..max] + "…";
    }
}
