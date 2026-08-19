using System.Text;

namespace App.Core.Storage;

public enum ChatAttachmentKind
{
    Image,
    Pdf,
    Text,
    Unsupported
}

/// <summary>
/// Classifies chat attachments so images/PDFs stay on the vision path and
/// markdown/source/text files can be inlined as UTF-8 for any model.
/// </summary>
public static class ChatAttachmentClassifier
{
    /// <summary>Default per-file character cap when inlining into the model request (~20k tokens).</summary>
    public const int MaxInlinedChars = 80_000;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdown", ".mkd",
        ".txt", ".text", ".log",
        ".csv", ".tsv",
        ".json", ".jsonc", ".json5",
        ".xml", ".xsl", ".xslt",
        ".yml", ".yaml",
        ".toml",
        ".ini", ".cfg", ".conf", ".config", ".env", ".properties",
        ".html", ".htm", ".xhtml",
        ".css", ".scss", ".sass", ".less",
        ".js", ".mjs", ".cjs", ".jsx",
        ".ts", ".tsx", ".mts", ".cts",
        ".cs", ".csx", ".razor", ".cshtml", ".fs", ".fsx", ".vb",
        ".py", ".pyi", ".rb", ".go", ".rs",
        ".java", ".kt", ".kts", ".swift",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".hh", ".cxx", ".m", ".mm",
        ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".bat", ".cmd",
        ".sql", ".graphql", ".gql", ".proto",
        ".vue", ".svelte", ".php", ".lua", ".r", ".pl", ".pm", ".scala", ".dart",
        ".tex", ".rst", ".adoc", ".asciidoc",
        ".gitignore", ".dockerignore", ".editorconfig",
        ".dockerfile",
        ".svg",
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".svg", ".ico", ".tif", ".tiff", ".heic", ".avif"
    };

    private static readonly HashSet<string> ExtensionlessTextNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dockerfile", "makefile", "license", "licence", "copying",
        "gemfile", "procfile", "jenkinsfile", "vagrantfile",
        "readme", "changelog", "authors", "contributors", "notice",
        "cmakelists.txt"
    };

    private static readonly HashSet<string> TextApplicationMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/json",
        "application/ld+json",
        "application/xml",
        "application/javascript",
        "application/ecmascript",
        "application/sql",
        "application/x-sql",
        "application/yaml",
        "application/x-yaml",
        "application/toml",
        "application/graphql",
        "application/x-sh",
        "application/x-shellscript",
        "application/x-powershell",
        "application/xhtml+xml",
        "application/rss+xml",
        "application/atom+xml",
        "application/x-www-form-urlencoded",
        "application/typescript",
    };

    private static readonly Dictionary<string, string> ExtensionToMime = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".tsv"] = "text/tab-separated-values",
        [".json"] = "application/json",
        [".jsonc"] = "application/json",
        [".xml"] = "application/xml",
        [".yml"] = "application/yaml",
        [".yaml"] = "application/yaml",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".css"] = "text/css",
        [".js"] = "text/javascript",
        [".mjs"] = "text/javascript",
        [".ts"] = "text/typescript",
        [".cs"] = "text/x-csharp",
        [".py"] = "text/x-python",
        [".sh"] = "application/x-sh",
        [".ps1"] = "application/x-powershell",
        [".sql"] = "application/sql",
        [".svg"] = "image/svg+xml",
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
    };

    private static readonly Dictionary<string, string> ExtensionToFence = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = "markdown",
        [".markdown"] = "markdown",
        [".csv"] = "csv",
        [".tsv"] = "tsv",
        [".json"] = "json",
        [".jsonc"] = "json",
        [".json5"] = "json",
        [".xml"] = "xml",
        [".yml"] = "yaml",
        [".yaml"] = "yaml",
        [".toml"] = "toml",
        [".ini"] = "ini",
        [".cfg"] = "ini",
        [".conf"] = "ini",
        [".env"] = "ini",
        [".html"] = "html",
        [".htm"] = "html",
        [".css"] = "css",
        [".scss"] = "scss",
        [".less"] = "less",
        [".js"] = "javascript",
        [".mjs"] = "javascript",
        [".cjs"] = "javascript",
        [".jsx"] = "jsx",
        [".ts"] = "typescript",
        [".tsx"] = "tsx",
        [".cs"] = "csharp",
        [".csx"] = "csharp",
        [".razor"] = "razor",
        [".cshtml"] = "razor",
        [".fs"] = "fsharp",
        [".vb"] = "vbnet",
        [".py"] = "python",
        [".rb"] = "ruby",
        [".go"] = "go",
        [".rs"] = "rust",
        [".java"] = "java",
        [".kt"] = "kotlin",
        [".kts"] = "kotlin",
        [".swift"] = "swift",
        [".c"] = "c",
        [".h"] = "c",
        [".cpp"] = "cpp",
        [".hpp"] = "cpp",
        [".cc"] = "cpp",
        [".m"] = "objectivec",
        [".sh"] = "bash",
        [".bash"] = "bash",
        [".zsh"] = "bash",
        [".ps1"] = "powershell",
        [".bat"] = "bat",
        [".cmd"] = "bat",
        [".sql"] = "sql",
        [".graphql"] = "graphql",
        [".gql"] = "graphql",
        [".proto"] = "protobuf",
        [".vue"] = "vue",
        [".svelte"] = "svelte",
        [".php"] = "php",
        [".lua"] = "lua",
        [".r"] = "r",
        [".pl"] = "perl",
        [".scala"] = "scala",
        [".dart"] = "dart",
        [".tex"] = "latex",
        [".rst"] = "rst",
        [".svg"] = "xml",
        [".dockerfile"] = "dockerfile",
    };

    public static ChatAttachmentKind Classify(Attachment att) =>
        Classify(att.Name, att.ContentType);

    public static ChatAttachmentKind Classify(string? fileName, string? contentType, byte[]? bytes = null)
    {
        var mime = (contentType ?? "").Trim();
        var semi = mime.IndexOf(';');
        if (semi > 0)
            mime = mime[..semi].Trim();

        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            // SVG is XML text and is more useful inlined than sent as a vision blob.
            if (mime.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
                return ChatAttachmentKind.Text;
            return ChatAttachmentKind.Image;
        }

        if (mime.Equals("application/pdf", StringComparison.OrdinalIgnoreCase))
            return ChatAttachmentKind.Pdf;

        if (IsTextMime(mime))
            return ChatAttachmentKind.Text;

        var ext = GetExtension(fileName);
        if (!string.IsNullOrEmpty(ext))
        {
            if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                return ChatAttachmentKind.Pdf;
            if (ext.Equals(".svg", StringComparison.OrdinalIgnoreCase))
                return ChatAttachmentKind.Text;
            if (ImageExtensions.Contains(ext))
                return ChatAttachmentKind.Image;
            if (TextExtensions.Contains(ext))
                return ChatAttachmentKind.Text;
        }

        if (IsExtensionlessTextName(fileName))
            return ChatAttachmentKind.Text;

        if (bytes is { Length: > 0 } && LooksLikeText(bytes))
            return ChatAttachmentKind.Text;

        return ChatAttachmentKind.Unsupported;
    }

    public static string GuessContentType(string? fileName, string? reportedMime)
    {
        var mime = (reportedMime ?? "").Trim();
        if (!string.IsNullOrEmpty(mime)
            && !mime.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            && !mime.Equals("binary/octet-stream", StringComparison.OrdinalIgnoreCase))
        {
            return mime;
        }

        var ext = GetExtension(fileName);
        if (!string.IsNullOrEmpty(ext) && ExtensionToMime.TryGetValue(ext, out var mapped))
            return mapped;

        if (IsExtensionlessTextName(fileName))
            return "text/plain";

        return string.IsNullOrEmpty(mime) ? "application/octet-stream" : mime;
    }

    public static bool TryDecodeText(string? dataBase64, out string text)
    {
        text = "";
        if (string.IsNullOrWhiteSpace(dataBase64))
            return false;
        try
        {
            var bytes = Convert.FromBase64String(dataBase64);
            return TryDecodeText(bytes, out text);
        }
        catch
        {
            return false;
        }
    }

    public static bool TryDecodeText(byte[] bytes, out string text)
    {
        text = "";
        if (bytes == null || bytes.Length == 0)
            return false;
        if (!LooksLikeText(bytes))
            return false;

        int offset = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            offset = 3;

        text = Utf8.GetString(bytes, offset, bytes.Length - offset);
        return true;
    }

    public static bool WouldTruncate(byte[] bytes, int maxChars = MaxInlinedChars)
    {
        if (bytes == null || maxChars <= 0)
            return false;
        // UTF-8: length in chars <= byte length; avoid a full decode just to warn.
        return bytes.Length > maxChars;
    }

    public static string FenceLanguage(string? fileName)
    {
        var ext = GetExtension(fileName);
        if (!string.IsNullOrEmpty(ext) && ExtensionToFence.TryGetValue(ext, out var lang))
            return lang;

        var name = Path.GetFileName(fileName ?? "");
        if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
            return "dockerfile";
        if (name.Equals("Makefile", StringComparison.OrdinalIgnoreCase))
            return "makefile";
        return "";
    }

    public static string FormatInlinedText(string? fileName, string text, int maxChars, out bool truncated)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "file" : fileName.Trim();
        var body = text ?? "";
        int originalLen = body.Length;
        truncated = maxChars > 0 && body.Length > maxChars;
        if (truncated)
            body = body[..maxChars];

        var fence = ChooseFence(body);
        var lang = FenceLanguage(name);
        var sb = new StringBuilder(body.Length + 96);
        sb.Append("[Attached file: ").Append(name).Append(']');
        if (truncated)
        {
            sb.Append("\n… truncated after ")
              .Append(maxChars.ToString("N0"))
              .Append(" characters (file is ")
              .Append(originalLen.ToString("N0"))
              .Append(" characters).");
        }

        sb.Append('\n').Append(fence).Append(lang).Append('\n')
          .Append(body).Append('\n').Append(fence);
        return sb.ToString();
    }

    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }

    public static string UnsupportedMessage() =>
        "Chat can read images, PDFs, and text/code files (.md, .txt, .cs, .json, and other source files). This file type is not supported yet.";

    private static bool IsTextMime(string mime)
    {
        if (string.IsNullOrEmpty(mime))
            return false;
        if (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            return true;
        return TextApplicationMimes.Contains(mime);
    }

    private static bool IsExtensionlessTextName(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? "");
        if (string.IsNullOrEmpty(name))
            return false;
        if (ExtensionlessTextNames.Contains(name))
            return true;
        // ".gitignore" etc. — GetExtension returns the whole name.
        return name.StartsWith('.') && TextExtensions.Contains(name);
    }

    private static string GetExtension(string? fileName)
    {
        var name = Path.GetFileName(fileName ?? "");
        if (string.IsNullOrEmpty(name))
            return "";
        if (name.StartsWith('.') && !name.AsSpan(1).Contains('.'))
            return name; // ".gitignore"
        return Path.GetExtension(name);
    }

    /// <summary>
    /// Reject NULs and high control-character ratios. UTF-8 markdown/code always passes.
    /// </summary>
    internal static bool LooksLikeText(byte[] bytes)
    {
        int sample = Math.Min(bytes.Length, 8192);
        int control = 0;
        for (int i = 0; i < sample; i++)
        {
            byte b = bytes[i];
            if (b == 0)
                return false;
            if (b < 0x09 || (b > 0x0D && b < 0x20))
                control++;
        }

        return control * 20 < sample; // under 5% C0 controls
    }

    private static string ChooseFence(string body)
    {
        int longest = 0;
        int run = 0;
        foreach (var ch in body)
        {
            if (ch == '`')
            {
                run++;
                if (run > longest)
                    longest = run;
            }
            else
            {
                run = 0;
            }
        }

        int n = Math.Max(3, longest + 1);
        return new string('`', n);
    }
}
