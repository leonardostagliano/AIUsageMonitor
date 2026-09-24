using System.Globalization;
using System.Text.Json;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Sessions;

public enum CloudSessionStatus { Working, NeedsInput, Idle, Failed, Archived }

/// <summary>Cumulative usage a cloud session reports (<c>external_metadata.usage</c>).</summary>
public sealed record CloudUsage(long Input, long Output, long CacheRead, long CacheWrite)
{
    public TokenUsage ToTokenUsage() => new(Input, Output, CacheRead, CacheWrite);

    /// <summary>
    /// The same usage priced under <paramref name="model"/> at standard rates. Claude Code writes only one-hour cache,
    /// so the cache writes go there; without a model the entry stays unpriced ("costo n/d").
    /// </summary>
    public UsageLedger ToLedger(string? model) => UsageLedger.From(
    [
        KeyValuePair.Create(new UsageKey(model ?? "", PriceTier.Standard, null, 0), new LedgerTokens(Input, Output, CacheRead, 0, CacheWrite))
    ]);
}

/// <summary>
/// A Claude Code session running in Anthropic's cloud — started from claude.ai/code, the desktop or mobile app — or the
/// latest run of a routine.
/// </summary>
public sealed record CloudSession(
    string Id,
    string? Title,
    CloudSessionStatus Status,
    DateTimeOffset LastActivity,
    SessionOrigin Origin,
    string? Message = null,
    string? Model = null,
    CloudUsage? Usage = null,
    string? EnvironmentKind = null)
{
    /// <summary>"session_X" and "cse_X" are two spellings of the same session: the key is X.</summary>
    public static string KeyOf(string id) =>
        id.StartsWith("session_", StringComparison.Ordinal) ? id[8..]
        : id.StartsWith("cse_", StringComparison.Ordinal) ? id[4..]
        : id;

    /// <summary>The page of the session on claude.ai.</summary>
    public static string WebUrl(string id) => "https://claude.ai/code/session_" + Uri.EscapeDataString(KeyOf(id));

    private const int MaxDesktopKey = 128;

    /// <summary>
    /// The same page as <see cref="WebUrl"/> in the Claude desktop app: its <c>claude://claude.ai</c> links mirror the
    /// paths of claude.ai (documented for <c>/chat/&lt;id&gt;</c> and <c>/project/&lt;id&gt;</c>, not yet for a Code
    /// session). Null unless the key is a plain session id (ASCII letters, digits, '_' and '-', at most 128 of them;
    /// real keys are about 26): the shell hands this link to another app, so it is never built from anything else,
    /// and any other id opens in the browser only.
    /// </summary>
    public static string? DesktopUrl(string id)
    {
        var key = KeyOf(id);
        return key.Length is > 0 and <= MaxDesktopKey && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? "claude://claude.ai/code/session_" + key
            : null;
    }
}

/// <summary>
/// Reads the answers of the sessions API (<c>GET /v1/code/sessions</c>) and of the routines API
/// (<c>GET /v1/code/triggers</c>). Both are internal APIs of claude.ai, so the reader is lenient: it accepts the fields
/// under the names the Claude Code CLI reads (<c>worker_status</c>, <c>last_event_at</c>, <c>config.model</c>) and
/// under the older ones (<c>session_status</c> with or without the <c>SESSION_STATUS_</c> prefix, <c>updated_at</c>,
/// <c>session_context.model</c>), and skips an entry it cannot make sense of instead of failing the whole list.
/// </summary>
public static class CloudSessionParser
{
    public static IReadOnlyList<CloudSession> ParseSessions(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var sessions = new List<CloudSession>();
        foreach (var item in Items(doc.RootElement))
        {
            if (item.ValueKind != JsonValueKind.Object || Text(item, "id") is not { } id) continue;
            // A Remote Control session is a local CLI process the hooks and the registry already report.
            var kind = Text(item, "environment_kind");
            if (kind == "bridge") continue;
            var metadata = Object(item, "external_metadata");
            var when = Time(item, "last_event_at") ?? Time(item, "updated_at") ?? Time(item, "created_at");
            if (when is null) continue;
            var status = StatusOf(item, metadata);
            sessions.Add(new CloudSession(id, Text(item, "title"), status, when.Value, SessionOrigin.Cloud,
                MessageOf(item, metadata, status),
                Text(Object(item, "config"), "model") ?? Text(Object(item, "session_context"), "model") ?? Text(metadata, "last_served_model"),
                UsageOf(Object(metadata, "usage")), kind));
        }
        return sessions;
    }

    /// <summary>The latest run of every routine that has one, as a session of origin <see cref="SessionOrigin.Routine"/>.</summary>
    public static IReadOnlyList<CloudSession> ParseRoutineRuns(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var runs = new List<CloudSession>();
        foreach (var item in Items(doc.RootElement))
        {
            if (item.ValueKind != JsonValueKind.Object || Object(item, "last_run") is not { } run) continue;
            if (Text(run, "session_id") is not { } sessionId) continue;
            var fired = Time(run, "fired_at");
            var finished = Time(run, "finished_at");
            if ((finished ?? fired) is not { } when) continue;
            var status = Strip(Text(run, "status"), "routine_run_status_") switch
            {
                "running" or "pending" or "queued" or "started" or "in_progress" => CloudSessionStatus.Working,
                "failed" or "error" or "timed_out" or "timeout" => CloudSessionStatus.Failed,
                "succeeded" or "success" or "completed" or "cancelled" or "canceled" or "skipped" => CloudSessionStatus.Idle,
                _ => finished is null ? CloudSessionStatus.Working : CloudSessionStatus.Idle
            };
            var reason = Strip(Text(run, "failure_reason"), "routine_run_failure_reason_");
            var message = status == CloudSessionStatus.Failed
                ? reason is null or "unspecified" ? "Routine non riuscita" : $"Routine non riuscita ({reason.Replace('_', ' ')})"
                : null;
            runs.Add(new CloudSession(sessionId, Text(item, "name"), status, when, SessionOrigin.Routine, message));
        }
        return runs;
    }

    /// <summary>
    /// The sessions and the routine runs as one list: a run the sessions list also returns keeps the richer session
    /// entry, marked as a routine and named after it when the session has no title of its own.
    /// </summary>
    public static IReadOnlyList<CloudSession> Merge(IReadOnlyList<CloudSession> sessions, IReadOnlyList<CloudSession> runs)
    {
        var byKey = new Dictionary<string, CloudSession>(StringComparer.Ordinal);
        foreach (var session in sessions) byKey[CloudSession.KeyOf(session.Id)] = session;
        foreach (var run in runs)
        {
            var key = CloudSession.KeyOf(run.Id);
            byKey[key] = byKey.TryGetValue(key, out var session)
                ? session with { Origin = SessionOrigin.Routine, Title = string.IsNullOrWhiteSpace(session.Title) ? run.Title : session.Title }
                : run;
        }
        return byKey.Values.ToList();
    }

    private static CloudSessionStatus StatusOf(JsonElement item, JsonElement? metadata)
    {
        var overall = Strip(Text(item, "status"), "session_status_");
        if (overall == "archived") return CloudSessionStatus.Archived;
        if (overall is "failed" or "error") return CloudSessionStatus.Failed;
        var worker = Strip(Text(item, "worker_status") ?? Text(item, "session_status"), "session_status_");
        if (worker == "archived") return CloudSessionStatus.Archived;
        if (worker is "failed" or "error") return CloudSessionStatus.Failed;
        if (worker == "requires_action" || Object(metadata, "pending_action") is not null) return CloudSessionStatus.NeedsInput;
        return worker is "running" or "working" ? CloudSessionStatus.Working : CloudSessionStatus.Idle;
    }

    private static string? MessageOf(JsonElement item, JsonElement? metadata, CloudSessionStatus status)
    {
        if (status == CloudSessionStatus.NeedsInput)
            return Text(Object(metadata, "pending_action"), "tool_name") is { } tool ? $"Permesso richiesto: {tool}" : "Input richiesto";
        if (status == CloudSessionStatus.Failed) return "Sessione cloud non riuscita";
        // What the last turn did, as the apps show it under the title.
        return Text(Object(item, "post_turn_summary"), "status_detail")
            ?? Text(Object(metadata, "post_turn_summary"), "status_detail");
    }

    private static CloudUsage? UsageOf(JsonElement? usage)
    {
        if (usage is null) return null;
        var input = Count(usage, "input_tokens");
        var output = Count(usage, "output_tokens");
        var cacheRead = Count(usage, "cache_read_tokens") ?? Count(usage, "cache_read_input_tokens");
        var cacheWrite = Count(usage, "cache_write_tokens") ?? Count(usage, "cache_creation_input_tokens");
        if (input is null && output is null && cacheRead is null && cacheWrite is null) return null;
        return new CloudUsage(input ?? 0, output ?? 0, cacheRead ?? 0, cacheWrite ?? 0);
    }

    /// <summary>The entries of a list answer: <c>{"data": [...]}</c>, or a bare array.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return root.EnumerateArray();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            return data.EnumerateArray();
        return [];
    }

    private static string? Strip(string? value, string prefix)
    {
        if (value is null) return null;
        var lower = value.ToLowerInvariant();
        return lower.StartsWith(prefix, StringComparison.Ordinal) ? lower[prefix.Length..] : lower;
    }

    private static JsonElement? Object(JsonElement? obj, string name) =>
        obj is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Object ? p : null;

    private static string? Text(JsonElement? obj, string name) =>
        obj is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            && p.GetString() is { } s && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static long? Count(JsonElement? obj, string name) =>
        obj is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            && p.TryGetInt64(out var v) && v >= 0 ? v : null;

    private static DateTimeOffset? Time(JsonElement? obj, string name) =>
        Text(obj, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t) ? t : null;
}
