using System.Text.RegularExpressions;
using App.Core.Storage;

namespace App.Shared.Services.Lemonade;

/// <summary>
/// Extracts embedded media from Lemonade Omni (and similar) assistant responses.
/// Omni server-side tools embed images as markdown data-URIs and speech as
/// <c>&lt;audio&gt;data:audio/...;base64,...&lt;/audio&gt;</c>.
/// </summary>
public static partial class OmniMediaExtractor
{
    // ![alt](data:image/png;base64,AAAA...)
    [GeneratedRegex(
        @"!\[([^\]]*)\]\((data:image\/([a-zA-Z0-9.+-]+);base64,([A-Za-z0-9+/=\s]+))\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownImageRegex();

    // <img ... src="data:image/png;base64,..." ...>
    [GeneratedRegex(
        @"<img\b[^>]*\bsrc\s*=\s*[""'](data:image\/([a-zA-Z0-9.+-]+);base64,([A-Za-z0-9+/=\s]+))[""'][^>]*/?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlImageRegex();

    // <audio>data:audio/mpeg;base64,...</audio> or with attributes
    [GeneratedRegex(
        @"<audio\b[^>]*>\s*(data:audio\/([a-zA-Z0-9.+-]+);base64,([A-Za-z0-9+/=\s]+))\s*</audio>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AudioInnerRegex();

    // <audio src="data:audio/..." ...>
    [GeneratedRegex(
        @"<audio\b[^>]*\bsrc\s*=\s*[""'](data:audio\/([a-zA-Z0-9.+-]+);base64,([A-Za-z0-9+/=\s]+))[""'][^>]*(?:/>|>\s*</audio>)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AudioSrcRegex();

    public sealed record ExtractResult(
        string CleanText,
        List<Attachment> Attachments,
        int ImageCount,
        int AudioCount);

    public static bool LooksLikeEmbeddedMedia(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        return content.Contains("data:image/", StringComparison.OrdinalIgnoreCase)
               || content.Contains("data:audio/", StringComparison.OrdinalIgnoreCase);
    }

    public static ExtractResult Extract(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return new ExtractResult("", new List<Attachment>(), 0, 0);

        var attachments = new List<Attachment>();
        var text = content;
        int img = 0;
        int audio = 0;
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

        text = MarkdownImageRegex().Replace(text, m =>
        {
            var alt = m.Groups[1].Value;
            var subtype = NormalizeImageSubtype(m.Groups[3].Value);
            var b64 = CompactBase64(m.Groups[4].Value);
            if (string.IsNullOrWhiteSpace(b64)) return m.Value;
            img++;
            var name = $"omni-image-{stamp}-{img}.{subtype}";
            attachments.Add(new Attachment(name, $"image/{subtype}", b64, EstimateBytes(b64)));
            return string.IsNullOrWhiteSpace(alt) ? $"[Image {img}]" : $"[Image {img}: {alt}]";
        });

        text = HtmlImageRegex().Replace(text, m =>
        {
            var subtype = NormalizeImageSubtype(m.Groups[2].Value);
            var b64 = CompactBase64(m.Groups[3].Value);
            if (string.IsNullOrWhiteSpace(b64)) return m.Value;
            img++;
            var name = $"omni-image-{stamp}-{img}.{subtype}";
            attachments.Add(new Attachment(name, $"image/{subtype}", b64, EstimateBytes(b64)));
            return $"[Image {img}]";
        });

        text = AudioInnerRegex().Replace(text, m =>
        {
            var subtype = NormalizeAudioSubtype(m.Groups[2].Value);
            var b64 = CompactBase64(m.Groups[3].Value);
            if (string.IsNullOrWhiteSpace(b64)) return m.Value;
            audio++;
            var ext = subtype is "mpeg" or "mp3" ? "mp3" : subtype;
            var name = $"omni-speech-{stamp}-{audio}.{ext}";
            var ct = subtype is "mpeg" or "mp3" ? "audio/mpeg" : $"audio/{subtype}";
            attachments.Add(new Attachment(name, ct, b64, EstimateBytes(b64)));
            return $"[Audio {audio}]";
        });

        text = AudioSrcRegex().Replace(text, m =>
        {
            var subtype = NormalizeAudioSubtype(m.Groups[2].Value);
            var b64 = CompactBase64(m.Groups[3].Value);
            if (string.IsNullOrWhiteSpace(b64)) return m.Value;
            audio++;
            var ext = subtype is "mpeg" or "mp3" ? "mp3" : subtype;
            var name = $"omni-speech-{stamp}-{audio}.{ext}";
            var ct = subtype is "mpeg" or "mp3" ? "audio/mpeg" : $"audio/{subtype}";
            attachments.Add(new Attachment(name, ct, b64, EstimateBytes(b64)));
            return $"[Audio {audio}]";
        });

        // Collapse excessive blank lines left by removals.
        text = Regex.Replace(text, @"[ \t]+\n", "\n");
        text = Regex.Replace(text, @"\n{3,}", "\n\n").Trim();

        return new ExtractResult(text, attachments, img, audio);
    }

    private static string CompactBase64(string raw) =>
        Regex.Replace(raw ?? "", @"\s+", "");

    private static string NormalizeImageSubtype(string raw)
    {
        var s = (raw ?? "png").Trim().ToLowerInvariant();
        if (s is "jpg") return "jpeg";
        if (s is "jpeg" or "png" or "gif" or "webp" or "bmp") return s;
        return "png";
    }

    private static string NormalizeAudioSubtype(string raw)
    {
        var s = (raw ?? "mpeg").Trim().ToLowerInvariant();
        if (s is "mp3") return "mpeg";
        if (s is "mpeg" or "wav" or "ogg" or "opus" or "webm" or "mp4") return s;
        return "mpeg";
    }

    private static long EstimateBytes(string b64) => (long)(b64.Length * 3L / 4L);
}
