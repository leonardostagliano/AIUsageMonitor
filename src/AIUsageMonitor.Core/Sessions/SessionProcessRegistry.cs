using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Sessions;

public enum ProcessState { Alive, Dead, Unknown }

/// <summary>What a probe saw of a pid: its state and, for a live process, its creation time (FILETIME, UTC).</summary>
public readonly record struct ProcessSnapshot(ProcessState State, long? StartedAtFileTime = null)
{
    public static readonly ProcessSnapshot Dead = new(ProcessState.Dead);
    public static readonly ProcessSnapshot Unknown = new(ProcessState.Unknown);
    public static ProcessSnapshot Alive(long? startedAtFileTime) => new(ProcessState.Alive, startedAtFileTime);
}

/// <summary>
/// Looks a pid up in the operating system. <see cref="ProcessState.Unknown"/> is for a process that exists but cannot be
/// queried (another user, a protected process): never a reason to end a session.
/// </summary>
public interface IProcessProbe
{
    ProcessSnapshot Query(int pid);
}

/// <summary>Who tied a session to its process: the hook's process walk, or Claude Code's own session registry.</summary>
public enum BindingSource { Terminal, Registry }

/// <summary>The process that hosts a session: its pid and creation time, so a recycled pid is never taken for it.</summary>
public sealed record SessionProcess(int Pid, long? StartedAtFileTime, BindingSource Source);

/// <summary>
/// Ties each session to the process of the agent that runs it, and tells which sessions have lost it. Closing a
/// terminal (or a crash, or a reboot) kills the agent before it can fire <c>SessionEnd</c>: without this the session
/// stays in the notch until the 12-hour sweep, and is replayed from the events file at every restart of the app.
/// </summary>
/// <remarks>
/// The bindings are saved next to the other app data, so a session replayed after a restart of the app is still tied
/// to its process. A session found dead is remembered for <see cref="EndedMemory"/> (longer than the replay window):
/// its events are replayed again at the next start, and without the memory it would come back with no process to
/// check. A later event (the session resumed in a new process) brings it back to life.
/// <para>Thread-safe: the pump sweeps on its thread, the terminal registry binds from the thread pool.</para>
/// </remarks>
public sealed class SessionProcessRegistry
{
    /// <summary>How far two readings of the creation time of the same process may differ.</summary>
    public static readonly long StartToleranceFileTime = TimeSpan.FromSeconds(1).Ticks;

    /// <summary>How long a session found dead is remembered: more than the 24-hour replay window.</summary>
    public static readonly TimeSpan EndedMemory = TimeSpan.FromHours(48);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string? _stateFile;
    private readonly IProcessProbe _probe;
    private readonly IClock _clock;
    private readonly Dictionary<(AgentKind Agent, string SessionId), SessionProcess> _bound = new();
    private readonly Dictionary<(AgentKind Agent, string SessionId), DateTimeOffset> _ended = new();
    // Seen dead once: a second sweep confirms it, so one odd answer of the probe never ends a live session.
    private readonly HashSet<(AgentKind Agent, string SessionId)> _suspect = [];
    private readonly object _gate = new();

    /// <param name="stateFile">Where the bindings are kept across restarts; null keeps them in memory only.</param>
    public SessionProcessRegistry(string? stateFile, IProcessProbe probe, IClock clock)
    {
        _stateFile = stateFile;
        _probe = probe;
        _clock = clock;
    }

    /// <summary>Where IO errors go (the App wires a FileLogger); never rethrown.</summary>
    public Action<Exception>? OnError { get; init; }

    /// <summary>The binding of a session, null when it has none.</summary>
    public SessionProcess? Get(AgentKind agent, string sessionId)
    {
        lock (_gate) return _bound.TryGetValue((agent, sessionId), out var process) ? process : null;
    }

    /// <summary>Reads the saved bindings. A missing or unreadable file starts empty.</summary>
    public void Load()
    {
        if (_stateFile is null || !File.Exists(_stateFile)) return;
        try
        {
            var state = JsonSerializer.Deserialize<StateDto>(File.ReadAllText(_stateFile), JsonOptions);
            lock (_gate)
            {
                foreach (var b in state?.Bound ?? [])
                {
                    if (!AgentKindExtensions.TryParseKey(b.Agent, out var agent) || string.IsNullOrEmpty(b.SessionId) || b.Pid <= 0) continue;
                    _bound[(agent, b.SessionId)] = new SessionProcess(b.Pid, b.StartedAt,
                        b.Source == "registry" ? BindingSource.Registry : BindingSource.Terminal);
                }
                foreach (var e in state?.Ended ?? [])
                {
                    if (!AgentKindExtensions.TryParseKey(e.Agent, out var agent) || string.IsNullOrEmpty(e.SessionId)) continue;
                    _ended[(agent, e.SessionId)] = e.At;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Report(ex);
        }
    }

    /// <summary>
    /// Ties a session to the live process <paramref name="pid"/>. The creation time is read now when the caller has
    /// none; a pid the probe already sees dead is not bound. A binding from the terminal walk never replaces one from
    /// Claude Code's registry that is still alive: the registry names the process itself, the walk only infers it.
    /// Returns true when the binding changed.
    /// </summary>
    public bool Bind(AgentKind agent, string sessionId, int pid, BindingSource source, long? startedAtFileTime = null)
    {
        if (pid <= 0 || string.IsNullOrEmpty(sessionId)) return false;
        if (startedAtFileTime is null)
        {
            var seen = _probe.Query(pid);
            if (seen.State == ProcessState.Dead) return false;
            startedAtFileTime = seen.StartedAtFileTime;
        }

        var key = (agent, sessionId);
        var process = new SessionProcess(pid, startedAtFileTime, source);
        lock (_gate)
        {
            _suspect.Remove(key);
            var revived = _ended.Remove(key);
            if (_bound.TryGetValue(key, out var current))
            {
                if (current == process) { if (revived) Save(); return revived; }
                if (current.Source == BindingSource.Registry && source == BindingSource.Terminal && current.Pid != pid
                    && Check(current) != ProcessState.Dead)
                {
                    if (revived) Save();
                    return revived;
                }
            }
            _bound[key] = process;
            Save();
            return true;
        }
    }

    /// <summary>
    /// The sessions whose process is gone: a bound process the probe sees dead (or recycled) in two sweeps in a row,
    /// or a session already found dead whose events are being replayed. With <paramref name="confirm"/> false one
    /// sweep is enough — the startup sweep, where the replay has only restored what the events file remembers.
    /// Their bindings are dropped and the sessions remembered as ended. Unbound sessions and cloud sessions are never
    /// returned.
    /// </summary>
    public IReadOnlyList<SessionState> FindEnded(IEnumerable<SessionState> sessions, bool confirm = true)
    {
        var ended = new List<SessionState>();
        var now = _clock.UtcNow;
        lock (_gate)
        {
            var changed = false;
            foreach (var session in sessions)
            {
                if (session.Origin is SessionOrigin.Cloud or SessionOrigin.Routine) continue;
                var key = (session.Agent, session.SessionId);
                if (_ended.TryGetValue(key, out var endedAt) && session.LastEventAt <= endedAt)
                {
                    ended.Add(session);
                    continue;
                }
                if (!_bound.TryGetValue(key, out var process)) continue;
                if (Check(process) != ProcessState.Dead)
                {
                    _suspect.Remove(key);
                    continue;
                }
                if (confirm && _suspect.Add(key)) continue;

                _suspect.Remove(key);
                _bound.Remove(key);
                _ended[key] = now;
                changed = true;
                ended.Add(session);
            }
            if (changed) Save();
        }
        return ended;
    }

    /// <summary>Drops the bindings of sessions no longer tracked and the ended sessions older than <see cref="EndedMemory"/>.</summary>
    public void Prune(IEnumerable<SessionState> sessions)
    {
        var live = sessions.Select(s => (s.Agent, s.SessionId)).ToHashSet();
        var cutoff = _clock.UtcNow - EndedMemory;
        lock (_gate)
        {
            var changed = false;
            foreach (var key in _bound.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _bound.Remove(key);
                _suspect.Remove(key);
                changed = true;
            }
            foreach (var key in _ended.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList())
            {
                _ended.Remove(key);
                changed = true;
            }
            if (changed) Save();
        }
    }

    /// <summary>Alive, dead, or unknown; a live pid created at another moment is a recycled pid, so dead.</summary>
    public ProcessState Check(SessionProcess process)
    {
        var seen = _probe.Query(process.Pid);
        if (seen.State != ProcessState.Alive) return seen.State;
        if (process.StartedAtFileTime is { } expected && seen.StartedAtFileTime is { } actual
            && Math.Abs(expected - actual) > StartToleranceFileTime)
            return ProcessState.Dead;
        return ProcessState.Alive;
    }

    /// <summary>Writes the state atomically; called under the lock. An IO error is reported and the state stays in memory.</summary>
    private void Save()
    {
        if (_stateFile is null) return;
        try
        {
            var dto = new StateDto
            {
                Bound = _bound.Select(kv => new BoundDto
                {
                    Agent = kv.Key.Agent.Key(), SessionId = kv.Key.SessionId, Pid = kv.Value.Pid, StartedAt = kv.Value.StartedAtFileTime,
                    Source = kv.Value.Source == BindingSource.Registry ? "registry" : "terminal"
                }).ToList(),
                Ended = _ended.Select(kv => new EndedDto { Agent = kv.Key.Agent.Key(), SessionId = kv.Key.SessionId, At = kv.Value }).ToList()
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFile)!);
            var tmp = _stateFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dto, JsonOptions));
            File.Move(tmp, _stateFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(ex);
        }
    }

    private void Report(Exception ex)
    {
        try { OnError?.Invoke(ex); } catch { /* a broken logger must not break the sweep */ }
    }

    private sealed class StateDto
    {
        [JsonPropertyName("bound")] public List<BoundDto>? Bound { get; set; }
        [JsonPropertyName("ended")] public List<EndedDto>? Ended { get; set; }
    }

    private sealed class BoundDto
    {
        [JsonPropertyName("agent")] public string? Agent { get; set; }
        [JsonPropertyName("session_id")] public string? SessionId { get; set; }
        [JsonPropertyName("pid")] public int Pid { get; set; }
        [JsonPropertyName("started_at")] public long? StartedAt { get; set; }
        [JsonPropertyName("source")] public string? Source { get; set; }
    }

    private sealed class EndedDto
    {
        [JsonPropertyName("agent")] public string? Agent { get; set; }
        [JsonPropertyName("session_id")] public string? SessionId { get; set; }
        [JsonPropertyName("at")] public DateTimeOffset At { get; set; }
    }
}
