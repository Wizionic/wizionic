using System.Text.RegularExpressions;

namespace App.Core.Speech;

/// <summary>Drops duplicated prefix text from overlapping STT windows.</summary>
public static class TranscriptOverlap
{
    private static readonly Regex WhisperJunk = new(
        @"\[(?:BLANK[_\s-]?AUDIO|Silence|INAUDIBLE|MUSIC|NOISE)\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Whisper often inserts newlines and tokens like [BLANK_AUDIO].
    /// Collapse those so dictation stays one wrapping paragraph.
    /// </summary>
    public static string NormalizeForInsert(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var t = WhisperJunk.Replace(text, " ");
        return Whitespace.Replace(t, " ").Trim();
    }

    public static string Dedup(string? previous, string? next)
    {
        var cur = (next ?? "").Trim();
        if (cur.Length == 0)
            return "";

        var prev = (previous ?? "").Trim();
        if (prev.Length == 0)
            return cur;

        var max = Math.Min(Math.Min(prev.Length, cur.Length), 96);
        for (var len = max; len >= 8; len--)
        {
            var suffix = prev[^len..];
            if (cur.StartsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return cur[len..].TrimStart();
        }

        var prevWords = prev.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var curWords = cur.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var take = Math.Min(4, Math.Min(prevWords.Length, curWords.Length));
        for (var n = take; n >= 2; n--)
        {
            var suffix = string.Join(' ', prevWords.TakeLast(n));
            var prefix = string.Join(' ', curWords.Take(n));
            if (suffix.Equals(prefix, StringComparison.OrdinalIgnoreCase))
                return string.Join(' ', curWords.Skip(n));
        }

        return cur;
    }
}
