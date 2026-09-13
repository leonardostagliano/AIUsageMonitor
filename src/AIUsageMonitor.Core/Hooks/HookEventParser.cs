using System.Globalization;
using System.Text.Json;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Hooks;

public static class HookEventParser
{
    /// <summary>Parses one line of events.jsonl; returns null for anything malformed or unknown.</summary>
    public static HookEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!AgentKindExtensions.TryParseKey(GetString(root, "agent"), out var agent)) return null;
            var evt = GetString(root, "event");
            var sessionId = GetString(root, "session_id");
            if (string.IsNullOrEmpty(evt) || string.IsNullOrEmpty(sessionId)) return null;
            if (!DateTimeOffset.TryParse(GetString(root, "ts"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ts)) return null;

            return new HookEvent(ts, agent, evt, sessionId,
                GetString(root, "cwd"), GetString(root, "notification_type"), GetString(root, "message"), GetString(root, "source"),
                GetString(root, "agent_id"), GetString(root, "agent_type"),
                GetString(root, "transcript_path"), GetString(root, "agent_transcript_path"),
                GetHost(root));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HostInfo? GetHost(JsonElement root)
    {
        if (!root.TryGetProperty("host", out var host) || host.ValueKind != JsonValueKind.Object) return null;
        return new HostInfo(
            GetInt32(host, "ppid"), GetString(host, "herdr_pane"), GetString(host, "wt_session"),
            GetString(host, "term_program"), GetInt32(host, "vscode_pid"));
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int? GetInt32(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var v) ? v : null;
}
