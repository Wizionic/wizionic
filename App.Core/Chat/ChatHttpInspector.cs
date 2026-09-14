using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace App.Core.Chat;

/// <summary>
/// Opt-in, session-memory log of chat HTTP (router / load / chat).
/// Never writes a file — chat bodies stay off disk unless the user Copies from Settings.
/// </summary>
public static class ChatHttpInspector
{
    public const int MaxCalls = 40;
    public const int MaxBodyChars = 200_000;

    private static readonly ConcurrentQueue<ChatHttpCall> Calls = new();
    private static readonly AsyncLocal<string?> Turn = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private static bool _enabled;

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
                Clear();
            else
                RecordSystem("Chat HTTP inspector ON (this session, memory only — not written to disk or chat history).");
            Changed?.Invoke();
        }
    }

    public static event Action? Changed;

    public static string? CurrentTurnId => Turn.Value;

    public static IDisposable BeginTurn()
    {
        var id = DateTime.Now.ToString("HHmmss", CultureInfo.InvariantCulture)
                 + "-" + Guid.NewGuid().ToString("N")[..6];
        Turn.Value = id;
        return new TurnScope();
    }

    public static IReadOnlyList<ChatHttpCall> Snapshot() => Calls.ToArray();

    public static IReadOnlyList<ChatHttpCall> ForTurn(string? turnId)
    {
        if (string.IsNullOrWhiteSpace(turnId))
            return [];
        return Calls.Where(c => c.TurnId == turnId).ToArray();
    }

    public static string SnapshotText()
    {
        var sb = new StringBuilder();
        foreach (var c in Calls)
        {
            sb.AppendLine(c.Format());
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public static string FormatTurn(string? turnId)
    {
        var list = ForTurn(turnId);
        if (list.Count == 0)
            return "";
        var sb = new StringBuilder();
        foreach (var c in list)
        {
            sb.AppendLine(c.Format());
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    public static void Clear()
    {
        while (Calls.TryDequeue(out _)) { }
        Changed?.Invoke();
    }

    public static void Record(
        string kind,
        string method,
        string url,
        object? requestBody,
        string? responseBody,
        int? status,
        int durationMs,
        string? model = null,
        int? ctxSize = null,
        string? error = null)
    {
        if (!_enabled)
            return;

        var call = new ChatHttpCall(
            TurnId: CurrentTurnId ?? "—",
            Kind: kind,
            At: DateTime.Now,
            Method: method,
            Url: url,
            Model: model,
            CtxSize: ctxSize,
            DurationMs: durationMs,
            Status: status,
            RequestBody: Truncate(ToJson(requestBody)),
            ResponseBody: Truncate(PrettyJsonOrRaw(responseBody)),
            Error: error);

        Calls.Enqueue(call);
        while (Calls.Count > MaxCalls && Calls.TryDequeue(out _)) { }
        try { Changed?.Invoke(); } catch { /* UI */ }
    }

    public static void RecordSystem(string message)
    {
        if (!_enabled)
            return;
        Calls.Enqueue(new ChatHttpCall(
            TurnId: CurrentTurnId ?? "—",
            Kind: "system",
            At: DateTime.Now,
            Method: "",
            Url: "",
            Model: null,
            CtxSize: null,
            DurationMs: 0,
            Status: null,
            RequestBody: message,
            ResponseBody: "",
            Error: null));
        while (Calls.Count > MaxCalls && Calls.TryDequeue(out _)) { }
        try { Changed?.Invoke(); } catch { }
    }

    private static string ToJson(object? body)
    {
        if (body is null)
            return "";
        if (body is string s)
            return PrettyJsonOrRaw(s);
        try
        {
            return JsonSerializer.Serialize(body, JsonOpts);
        }
        catch
        {
            return body.ToString() ?? "";
        }
    }

    private static string PrettyJsonOrRaw(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        try
        {
            using var doc = JsonDocument.Parse(text);
            return JsonSerializer.Serialize(doc.RootElement, JsonOpts);
        }
        catch
        {
            return text;
        }
    }

    private static string Truncate(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= MaxBodyChars)
            return text;
        return text[..MaxBodyChars] + "\n… truncated (" + text.Length + " chars)";
    }

    private sealed class TurnScope : IDisposable
    {
        public void Dispose() => Turn.Value = null;
    }
}

public sealed record ChatHttpCall(
    string TurnId,
    string Kind,
    DateTime At,
    string Method,
    string Url,
    string? Model,
    int? CtxSize,
    int DurationMs,
    int? Status,
    string RequestBody,
    string ResponseBody,
    string? Error)
{
    public string Format()
    {
        var sb = new StringBuilder();
        sb.Append(At.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture));
        sb.Append(" [").Append(Kind).Append(']');
        if (!string.IsNullOrEmpty(TurnId) && TurnId != "—")
            sb.Append(" turn=").Append(TurnId);
        if (DurationMs > 0)
            sb.Append(' ').Append(DurationMs).Append("ms");
        if (Status is int st)
            sb.Append(" HTTP ").Append(st);
        sb.AppendLine();
        if (!string.IsNullOrEmpty(Method) || !string.IsNullOrEmpty(Url))
            sb.Append(Method).Append(' ').AppendLine(Url);
        if (!string.IsNullOrWhiteSpace(Model))
            sb.Append("model=").Append(Model);
        if (CtxSize is int ctx)
            sb.Append(" ctx_size=").Append(ctx);
        if (!string.IsNullOrWhiteSpace(Model) || CtxSize is not null)
            sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(Error))
            sb.Append("error: ").AppendLine(Error);
        if (!string.IsNullOrWhiteSpace(RequestBody))
        {
            sb.AppendLine("--- request ---");
            sb.AppendLine(RequestBody);
        }
        if (!string.IsNullOrWhiteSpace(ResponseBody))
        {
            sb.AppendLine("--- response ---");
            sb.AppendLine(ResponseBody);
        }
        return sb.ToString().TrimEnd();
    }
}
