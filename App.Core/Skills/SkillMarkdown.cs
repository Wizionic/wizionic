using System.Text;

namespace App.Core.Skills;

/// <summary>
/// Parse/serialize Agent Skills SKILL.md (YAML frontmatter + markdown body).
/// Minimal YAML subset sufficient for the official skill fields + nested metadata map.
/// </summary>
public static class SkillMarkdown
{
    /// <summary>Official name rules: 1–64 chars, lowercase alnum + hyphens, no leading/trailing/consecutive hyphens.</summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length is < 1 or > 64) return false;
        if (name.StartsWith('-') || name.EndsWith('-')) return false;
        if (name.Contains("--", StringComparison.Ordinal)) return false;
        foreach (var c in name)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
                continue;
            return false;
        }
        return true;
    }

    public static string NormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim().ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        char prev = '\0';
        foreach (var c in s)
        {
            char ch = c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c
                : c is ' ' or '_' or '.' or '/' ? '-'
                : c == '-' ? '-'
                : '\0';
            if (ch == '\0') continue;
            if (ch == '-' && (sb.Length == 0 || prev == '-')) continue;
            sb.Append(ch);
            prev = ch;
        }
        while (sb.Length > 0 && sb[^1] == '-') sb.Length--;
        if (sb.Length > 64) sb.Length = 64;
        while (sb.Length > 0 && sb[^1] == '-') sb.Length--;
        return sb.ToString();
    }

    public static SkillDocument Parse(string markdown)
    {
        var doc = new SkillDocument();
        if (string.IsNullOrWhiteSpace(markdown))
            return doc;

        var text = markdown.Replace("\r\n", "\n").Replace('\r', '\n');
        if (!text.StartsWith("---", StringComparison.Ordinal))
        {
            doc.BodyMarkdown = text.Trim();
            return doc;
        }

        var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (end < 0)
        {
            doc.BodyMarkdown = text.Trim();
            return doc;
        }

        var yaml = text[3..end].Trim('\n');
        var bodyStart = end + 4; // past \n---
        if (bodyStart < text.Length && text[bodyStart] == '\n')
            bodyStart++;
        doc.BodyMarkdown = bodyStart < text.Length ? text[bodyStart..].TrimStart('\n') : "";

        ParseFrontmatter(yaml, doc);
        return doc;
    }

    public static string Serialize(SkillDocument doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.Append("name: ").AppendLine(YamlScalar(doc.Name ?? ""));
        sb.Append("description: ").AppendLine(YamlBlockOrScalar(doc.Description ?? ""));
        if (!string.IsNullOrWhiteSpace(doc.License))
            sb.Append("license: ").AppendLine(YamlScalar(doc.License));
        if (!string.IsNullOrWhiteSpace(doc.Compatibility))
            sb.Append("compatibility: ").AppendLine(YamlBlockOrScalar(doc.Compatibility));
        if (!string.IsNullOrWhiteSpace(doc.AllowedTools))
            sb.Append("allowed-tools: ").AppendLine(YamlScalar(doc.AllowedTools.Trim()));

        if (doc.Metadata is { Count: > 0 })
        {
            sb.AppendLine("metadata:");
            foreach (var kv in doc.Metadata.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value))
                    continue;
                sb.Append("  ").Append(kv.Key).Append(": ").AppendLine(YamlScalar(kv.Value));
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();
        var body = (doc.BodyMarkdown ?? "").Trim();
        if (body.Length > 0)
            sb.Append(body).AppendLine();
        return sb.ToString();
    }

    public static SkillValidationResult Validate(SkillDocument doc)
    {
        var r = new SkillValidationResult();
        if (string.IsNullOrWhiteSpace(doc.Name))
            r.Errors.Add("name is required.");
        else if (!IsValidName(doc.Name))
            r.Errors.Add("name must be 1–64 lowercase letters, numbers, and hyphens (no leading/trailing/consecutive hyphens).");

        if (string.IsNullOrWhiteSpace(doc.Description))
            r.Errors.Add("description is required.");
        else if (doc.Description.Length > 1024)
            r.Errors.Add("description must be at most 1024 characters.");

        if (doc.Compatibility is { Length: > 500 })
            r.Errors.Add("compatibility must be at most 500 characters.");

        if (string.IsNullOrWhiteSpace(doc.BodyMarkdown))
            r.Warnings.Add("Body is empty — add steps so the agent knows what to do.");

        var lines = (doc.BodyMarkdown ?? "").Split('\n').Length;
        if (lines > 500)
            r.Warnings.Add($"Body has {lines} lines (spec recommends under 500).");

        return r;
    }

    public static SkillValidationResult ValidateMarkdown(string markdown)
    {
        try
        {
            var doc = Parse(markdown);
            return Validate(doc);
        }
        catch (Exception ex)
        {
            var r = new SkillValidationResult();
            r.Errors.Add("Failed to parse SKILL.md: " + ex.Message);
            return r;
        }
    }

    /// <summary>Build a document from creator form fields.</summary>
    public static SkillDocument FromForm(
        string name,
        string description,
        string? license,
        string? compatibility,
        string? author,
        string? version,
        string? tags,
        string? allowedTools,
        string? triggerPhrases,
        string purpose,
        string steps,
        string examples,
        string notes,
        string? inputSchemaJson = null)
    {
        var doc = new SkillDocument
        {
            Name = NormalizeName(name),
            Description = (description ?? "").Trim(),
            License = string.IsNullOrWhiteSpace(license) ? null : license.Trim(),
            Compatibility = string.IsNullOrWhiteSpace(compatibility) ? null : compatibility.Trim(),
            AllowedTools = string.IsNullOrWhiteSpace(allowedTools) ? null : allowedTools.Trim()
        };

        if (!string.IsNullOrWhiteSpace(author)) doc.SetMeta("author", author.Trim());
        if (!string.IsNullOrWhiteSpace(version)) doc.SetMeta("version", version.Trim());
        if (!string.IsNullOrWhiteSpace(tags)) doc.SetMeta("tags", tags.Trim());
        if (!string.IsNullOrWhiteSpace(triggerPhrases)) doc.SetMeta("trigger-phrases", triggerPhrases.Trim());
        if (!string.IsNullOrWhiteSpace(inputSchemaJson)) doc.SetMeta("input-schema", inputSchemaJson.Trim());

        // Enrich description with trigger phrases if not already present
        if (!string.IsNullOrWhiteSpace(triggerPhrases) &&
            !doc.Description.Contains(triggerPhrases, StringComparison.OrdinalIgnoreCase))
        {
            var extra = " Triggers: " + triggerPhrases.Trim();
            if (doc.Description.Length + extra.Length <= 1024)
                doc.Description = doc.Description.TrimEnd() + extra;
        }

        var title = string.IsNullOrWhiteSpace(doc.Name)
            ? "Skill"
            : string.Join(' ', doc.Name.Split('-', StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));

        var body = new StringBuilder();
        body.Append("# ").AppendLine(title);
        body.AppendLine();
        if (!string.IsNullOrWhiteSpace(purpose))
        {
            body.AppendLine("## Purpose");
            body.AppendLine(purpose.Trim());
            body.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(steps))
        {
            body.AppendLine("## Steps");
            body.AppendLine(steps.Trim());
            body.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(examples))
        {
            body.AppendLine("## Examples");
            body.AppendLine(examples.Trim());
            body.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(notes))
        {
            body.AppendLine("## Notes");
            body.AppendLine(notes.Trim());
            body.AppendLine();
        }
        doc.BodyMarkdown = body.ToString().Trim();
        return doc;
    }

    private static void ParseFrontmatter(string yaml, SkillDocument doc)
    {
        var lines = yaml.Split('\n');
        string? currentKey = null;
        var block = new StringBuilder();
        bool inBlock = false;
        bool inMetadata = false;

        void FlushKey()
        {
            if (currentKey is null) return;
            var val = block.ToString().Trim();
            ApplyKey(doc, currentKey, val, inMetadata);
            currentKey = null;
            block.Clear();
            inBlock = false;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (inBlock)
            {
                // Indented continuation of folded/literal block, or plain multi-line scalar
                if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                {
                    block.AppendLine(line.TrimStart());
                    continue;
                }
                // Non-indented: end block
                FlushKey();
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (!line.StartsWith(' ') && !line.StartsWith('\t') && line.TrimEnd() == "metadata:")
            {
                FlushKey();
                inMetadata = true;
                continue;
            }

            var trimmed = line.TrimEnd();
            // metadata nested key
            if (inMetadata && (line.StartsWith("  ") || line.StartsWith("\t")))
            {
                var m = line.Trim();
                var colon = m.IndexOf(':');
                if (colon > 0)
                {
                    FlushKey();
                    currentKey = m[..colon].Trim();
                    var rest = m[(colon + 1)..].Trim();
                    if (rest is ">" or "|" or ">-" or "|-")
                    {
                        inBlock = true;
                    }
                    else
                    {
                        ApplyKey(doc, currentKey, Unquote(rest), inMetadata: true);
                        currentKey = null;
                    }
                }
                continue;
            }

            // Top-level key ends metadata
            if (inMetadata && !line.StartsWith(' ') && !line.StartsWith('\t'))
                inMetadata = false;

            var cIdx = trimmed.IndexOf(':');
            if (cIdx <= 0) continue;

            FlushKey();
            currentKey = trimmed[..cIdx].Trim();
            var valuePart = trimmed[(cIdx + 1)..].Trim();
            if (valuePart is ">" or "|" or ">-" or "|-")
            {
                inBlock = true;
            }
            else if (valuePart.Length == 0)
            {
                // empty → treat following indented as block
                inBlock = true;
            }
            else
            {
                ApplyKey(doc, currentKey, Unquote(valuePart), inMetadata: false);
                currentKey = null;
            }
        }

        FlushKey();
    }

    private static void ApplyKey(SkillDocument doc, string key, string value, bool inMetadata)
    {
        if (inMetadata)
        {
            if (!string.IsNullOrWhiteSpace(key))
                doc.Metadata[key] = value;
            return;
        }

        switch (key.ToLowerInvariant())
        {
            case "name":
                doc.Name = value.Trim();
                break;
            case "description":
                doc.Description = value.Trim();
                break;
            case "license":
                doc.License = value.Trim();
                break;
            case "compatibility":
                doc.Compatibility = value.Trim();
                break;
            case "allowed-tools":
            case "allowed_tools":
                doc.AllowedTools = value.Trim();
                break;
            case "metadata":
                // ignore bare key
                break;
            default:
                // Unknown top-level → metadata for round-trip friendliness
                doc.Metadata[key] = value;
                break;
        }
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 &&
            ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            return s[1..^1];
        return s;
    }

    private static string YamlScalar(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        // Use quotes when special chars present
        if (value.Contains(':') || value.Contains('#') || value.Contains('\n') ||
            value.Contains('"') || value.StartsWith(' ') || value.EndsWith(' ') ||
            value is "true" or "false" or "null" || value.StartsWith('>') || value.StartsWith('|'))
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
        }
        return value;
    }

    private static string YamlBlockOrScalar(string value)
    {
        if (value.Contains('\n') || value.Length > 80)
        {
            var sb = new StringBuilder();
            sb.AppendLine(">");
            foreach (var line in value.Replace("\r\n", "\n").Split('\n'))
                sb.Append("  ").AppendLine(line);
            return sb.ToString().TrimEnd();
        }
        return YamlScalar(value);
    }
}
