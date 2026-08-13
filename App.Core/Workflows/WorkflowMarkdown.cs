using System.Text;
using System.Text.RegularExpressions;

namespace App.Core.Workflows;

/// <summary>
/// Minimal YAML parse/serialize for wizionic.workflow/v1 (nested maps, scalars only).
/// </summary>
public static class WorkflowMarkdown
{
    public const string SchemaId = "wizionic.workflow/v1";

    private static readonly Regex IdPattern = new(
        @"^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){0,62}[a-z0-9]$|^[a-z0-9]$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsValidId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
        if (id.Contains("--") || id.StartsWith('-') || id.EndsWith('-')) return false;
        foreach (var c in id)
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or (>= 'A' and <= 'Z'))
                continue;
            return false;
        }
        return true;
    }

    public static string NormalizeId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim().ToLowerInvariant();
        var sb = new StringBuilder(s.Length);
        char prev = '\0';
        foreach (var c in s)
        {
            char ch = c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c
                : c is ' ' or '_' or '.' ? '-'
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

    public static WorkflowDocument Parse(string yaml)
    {
        var doc = new WorkflowDocument();
        if (string.IsNullOrWhiteSpace(yaml)) return doc;

        var lines = yaml.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        string section = "";
        foreach (var raw in lines)
        {
            var line = raw;
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;

            var indent = 0;
            while (indent < line.Length && line[indent] == ' ') indent++;
            var trimmed = line.Trim();
            var colon = trimmed.IndexOf(':');
            if (colon <= 0) continue;
            var key = trimmed[..colon].Trim();
            var val = Unquote(trimmed[(colon + 1)..].Trim());

            if (indent == 0)
            {
                section = key.ToLowerInvariant();
                switch (section)
                {
                    case "schema":
                        doc.Schema = string.IsNullOrEmpty(val) ? SchemaId : val;
                        break;
                    case "id":
                        doc.Id = NormalizeId(val);
                        break;
                    case "name":
                        doc.Name = val;
                        break;
                    case "enabled":
                        doc.Enabled = ParseBool(val, true);
                        break;
                    case "trigger":
                    case "orchestrator":
                    case "execute_skill":
                    case "calendar":
                        break;
                }
                continue;
            }

            if (indent >= 2)
            {
                switch (section)
                {
                    case "trigger":
                        if (key.Equals("type", StringComparison.OrdinalIgnoreCase))
                            doc.Trigger.Type = string.IsNullOrEmpty(val) ? "manual" : val.ToLowerInvariant();
                        else if (key.Equals("expression", StringComparison.OrdinalIgnoreCase))
                            doc.Trigger.Expression = val;
                        else if (key.Equals("timezone", StringComparison.OrdinalIgnoreCase))
                            doc.Trigger.Timezone = string.IsNullOrEmpty(val) ? "local" : val;
                        break;
                    case "orchestrator":
                        if (key.Equals("strategy", StringComparison.OrdinalIgnoreCase))
                            doc.Orchestrator.Strategy = string.IsNullOrEmpty(val) ? "fallback_chain" : val.ToLowerInvariant();
                        else if (key.Equals("preferred_model", StringComparison.OrdinalIgnoreCase))
                            doc.Orchestrator.PreferredModel = val;
                        else if (key.Equals("fallback_model", StringComparison.OrdinalIgnoreCase))
                            doc.Orchestrator.FallbackModel = val;
                        break;
                    case "execute_skill":
                        if (key.Equals("id", StringComparison.OrdinalIgnoreCase))
                            doc.ExecuteSkill.Id = val;
                        else if (key.Equals("user_message", StringComparison.OrdinalIgnoreCase))
                            doc.ExecuteSkill.UserMessage = val;
                        break;
                    case "calendar":
                        if (key.Equals("project", StringComparison.OrdinalIgnoreCase))
                            doc.Calendar.Project = ParseBool(val, true);
                        else if (key.Equals("title", StringComparison.OrdinalIgnoreCase))
                            doc.Calendar.Title = val;
                        else if (key.Equals("color", StringComparison.OrdinalIgnoreCase))
                            doc.Calendar.Color = string.IsNullOrEmpty(val) ? "#7c3aed" : val;
                        break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(doc.Name) && !string.IsNullOrWhiteSpace(doc.Id))
            doc.Name = doc.Id;
        return doc;
    }

    public static string Serialize(WorkflowDocument doc)
    {
        var sb = new StringBuilder();
        sb.Append("schema: ").AppendLine(YamlScalar(string.IsNullOrWhiteSpace(doc.Schema) ? SchemaId : doc.Schema));
        sb.Append("id: ").AppendLine(YamlScalar(doc.Id ?? ""));
        sb.Append("name: ").AppendLine(YamlScalar(doc.Name ?? doc.Id ?? ""));
        sb.Append("enabled: ").AppendLine(doc.Enabled ? "true" : "false");
        sb.AppendLine("trigger:");
        sb.Append("  type: ").AppendLine(YamlScalar(doc.Trigger.Type ?? "manual"));
        if (!string.IsNullOrWhiteSpace(doc.Trigger.Expression))
            sb.Append("  expression: ").AppendLine(YamlScalar(doc.Trigger.Expression));
        sb.Append("  timezone: ").AppendLine(YamlScalar(doc.Trigger.Timezone ?? "local"));
        sb.AppendLine("orchestrator:");
        sb.Append("  strategy: ").AppendLine(YamlScalar(doc.Orchestrator.Strategy ?? "fallback_chain"));
        if (!string.IsNullOrWhiteSpace(doc.Orchestrator.PreferredModel))
            sb.Append("  preferred_model: ").AppendLine(YamlScalar(doc.Orchestrator.PreferredModel));
        if (!string.IsNullOrWhiteSpace(doc.Orchestrator.FallbackModel))
            sb.Append("  fallback_model: ").AppendLine(YamlScalar(doc.Orchestrator.FallbackModel));
        sb.AppendLine("execute_skill:");
        sb.Append("  id: ").AppendLine(YamlScalar(doc.ExecuteSkill.Id ?? ""));
        if (!string.IsNullOrWhiteSpace(doc.ExecuteSkill.UserMessage))
            sb.Append("  user_message: ").AppendLine(YamlScalar(doc.ExecuteSkill.UserMessage));
        sb.AppendLine("calendar:");
        sb.Append("  project: ").AppendLine(doc.Calendar.Project ? "true" : "false");
        if (!string.IsNullOrWhiteSpace(doc.Calendar.Title))
            sb.Append("  title: ").AppendLine(YamlScalar(doc.Calendar.Title));
        sb.Append("  color: ").AppendLine(YamlScalar(doc.Calendar.Color ?? "#7c3aed"));
        return sb.ToString();
    }

    public static string? Validate(WorkflowDocument doc)
    {
        if (string.IsNullOrWhiteSpace(doc.Id) || !IsValidId(doc.Id))
            return "Workflow id is required (lowercase letters, numbers, hyphens).";
        if (string.IsNullOrWhiteSpace(doc.ExecuteSkill.Id))
            return "execute_skill.id (skill name) is required.";
        var t = (doc.Trigger.Type ?? "manual").ToLowerInvariant();
        if (t is "cron" && string.IsNullOrWhiteSpace(doc.Trigger.Expression))
            return "trigger.expression is required for cron workflows.";
        return null;
    }

    private static bool ParseBool(string? v, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(v)) return defaultValue;
        if (bool.TryParse(v, out var b)) return b;
        if (v is "1" or "yes" or "on") return true;
        if (v is "0" or "no" or "off") return false;
        return defaultValue;
    }

    private static string Unquote(string s)
    {
        if (s.Length >= 2 && ((s[0] == '"' && s[^1] == '"') || (s[0] == '\'' && s[^1] == '\'')))
            return s[1..^1];
        // strip inline comments for simple scalars
        var hash = s.IndexOf(" #", StringComparison.Ordinal);
        if (hash > 0) s = s[..hash].Trim();
        return s;
    }

    private static string YamlScalar(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        if (value.Contains(':') || value.Contains('#') || value.Contains('"') || value.Contains(' '))
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        return value;
    }
}
