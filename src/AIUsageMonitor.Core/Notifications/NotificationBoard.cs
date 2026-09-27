using AIUsageMonitor.Core.Infrastructure;

namespace AIUsageMonitor.Core.Notifications;

/// <summary>
/// The lifecycle of the notification cards (spec §5.4, §7, §8): one card per Key, replaced in place by a new state and
/// removed by a retire, a ✕ or its timer; at most <see cref="MaxVisible"/> on screen, persistent cards first; a queue
/// while Windows asks for quiet; the sound each operation plays. Pure and driven by <see cref="IClock"/>; not
/// thread-safe (the App drives it from the UI thread).
/// </summary>
public sealed class NotificationBoard
{
    public const int MaxVisible = 3;

    // The outcomes without a sound: nothing to redraw, or a stack to redraw.
    private static readonly BoardResult Unchanged = new(false, NotificationSoundKind.None);
    private static readonly BoardResult Redrawn = new(true, NotificationSoundKind.None);

    private readonly IClock _clock;
    private readonly List<Entry> _entries = [];
    // StateKey of a session card closed with ✕ or expired, per Key: that state does not come back until a retire.
    private readonly Dictionary<string, string> _closedStates = new(StringComparer.Ordinal);
    private IReadOnlyList<BoardEntry> _visible = [];
    private long _sequence;
    private bool _hovering;

    public NotificationBoard(IClock clock) => _clock = clock;

    public bool IsQuiet { get; private set; }

    /// <summary>
    /// The cards on screen, newest first: up to <see cref="MaxVisible"/>, chosen first among the persistent cards (newest
    /// first), then among the timed ones, so a finished turn never pushes out a pending permission. Empty while quiet.
    /// </summary>
    public IReadOnlyList<BoardEntry> Visible => _visible;

    /// <summary>Cards waiting for a free slot (the "+N altre" pill); 0 while quiet, when nothing is on screen.</summary>
    public int HiddenCount => IsQuiet ? 0 : _entries.Count - _visible.Count;

    /// <summary>No card at all: none on screen, none waiting, none queued.</summary>
    public bool IsEmpty => _entries.Count == 0;

    public BoardResult Apply(NotificationIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return intent switch
        {
            ShowIntent show => Show(show.Card),
            RetireIntent retire => Retire(retire.Key),
            _ => throw new ArgumentException($"Unknown notification intent {intent.GetType().Name}.", nameof(intent))
        };
    }

    /// <summary>✕ or a click: removes the card and remembers the state it showed.</summary>
    public BoardResult Dismiss(string key)
    {
        var entry = Find(key);
        if (entry is null) return Unchanged;
        Close(entry);
        Reflow(_clock.UtcNow);
        return Redrawn;
    }

    /// <summary>Removes the visible auto-close cards whose time is up, remembering their state like a ✕.</summary>
    public BoardResult Tick()
    {
        var now = _clock.UtcNow;
        var expired = _entries.Where(e => e.ExpiresAt is { } at && at <= now).ToList();
        if (expired.Count == 0) return Unchanged;
        foreach (var entry in expired) Close(entry);
        Reflow(now);
        return Redrawn;
    }

    /// <summary>The mouse over the stack freezes every running timer; leaving it resumes them where they stopped.</summary>
    public BoardResult SetHover(bool hovering)
    {
        if (_hovering == hovering) return Unchanged;
        _hovering = hovering;
        var now = _clock.UtcNow;
        var changed = false;
        foreach (var entry in _entries)
        {
            if (hovering && entry.ExpiresAt is { } at)
            {
                entry.Remaining = at > now ? at - now : TimeSpan.Zero;
                entry.ExpiresAt = null;
                changed = true;
            }
            else if (!hovering && entry.Remaining is { } left)
            {
                entry.ExpiresAt = now + left;
                entry.Remaining = null;
                changed = true;
            }
        }
        if (changed) Reflow(now);
        return changed ? Redrawn : Unchanged;
    }

    /// <summary>
    /// Quiet (Do not disturb, full screen): every card leaves the screen and new ones wait, silently. Leaving it shows
    /// only what still waits for the user: timed cards (finished turns, Info notices) are dropped as expired, and a
    /// single <see cref="NotificationSoundKind.Attention"/> plays if a card queued meanwhile asked for it and is still
    /// there.
    /// </summary>
    public BoardResult SetQuiet(bool quiet)
    {
        if (IsQuiet == quiet) return Unchanged;
        IsQuiet = quiet;
        var hadCards = _entries.Count > 0;
        var sound = NotificationSoundKind.None;
        if (!quiet)
        {
            foreach (var entry in _entries.Where(e => e.Card.AutoClose is not null).ToList()) Close(entry);
            // One sound for the whole queue, however many cards owe it.
            if (_entries.Any(e => e.SoundOwed)) sound = NotificationSoundKind.Attention;
            foreach (var entry in _entries) entry.SoundOwed = false;
        }
        Reflow(_clock.UtcNow);
        return new BoardResult(hadCards, sound);
    }

    private BoardResult Show(NotificationCard card)
    {
        var now = _clock.UtcNow;
        var entry = Find(card.Key);
        if (entry is not null && entry.Card.StateKey == card.StateKey)
        {
            // The same state of a session again (a token update, a repeated notification): nothing new to say.
            if (card.Kind != NotificationKind.Notice) return Unchanged;
            // An identical notice is renewed: its age and its timer start again, without a second sound.
            Restart(entry, card, now);
            Reflow(now);
            return Redrawn;
        }
        if (entry is null)
        {
            if (_closedStates.TryGetValue(card.Key, out var closed) && closed == card.StateKey) return Unchanged;
            _closedStates.Remove(card.Key);
            entry = new Entry(card);
            _entries.Add(entry);
        }
        Restart(entry, card, now);
        // Queued while quiet, a card that asks for attention owes its sound to the end of the quiet; a finished turn owes
        // nothing (it is dropped then). The latest state of the Key decides.
        entry.SoundOwed = IsQuiet && card.Sound == NotificationSoundKind.Attention;
        Reflow(now);
        return new BoardResult(true, IsQuiet ? NotificationSoundKind.None : card.Sound);
    }

    private BoardResult Retire(string key)
    {
        _closedStates.Remove(key);
        var entry = Find(key);
        if (entry is null) return Unchanged;
        _entries.Remove(entry);
        Reflow(_clock.UtcNow);
        return Redrawn;
    }

    private Entry? Find(string key) => _entries.Find(e => e.Card.Key == key);

    /// <summary>
    /// Removes a card closed by the user, by its timer or by the end of quiet. A session card remembers its state; a
    /// notice does not, so the same notice raised again later (another "Terminale non trovato") shows again.
    /// </summary>
    private void Close(Entry entry)
    {
        _entries.Remove(entry);
        if (entry.Card.Kind != NotificationKind.Notice) _closedStates[entry.Card.Key] = entry.Card.StateKey;
    }

    /// <summary>New content (or the same notice again): newest in the stack, age from now, timer to start afresh.</summary>
    private void Restart(Entry entry, NotificationCard card, DateTimeOffset now)
    {
        entry.Card = card;
        entry.ShownAt = now;
        entry.Sequence = ++_sequence;
        entry.ExpiresAt = null;
        entry.Remaining = null;
    }

    /// <summary>
    /// Picks the cards on screen and keeps the timers in step with it: a timed card gets its full time when it shows
    /// (frozen if the mouse is over the stack) and loses its timer when it leaves the screen.
    /// </summary>
    private void Reflow(DateTimeOffset now)
    {
        List<Entry> shown = IsQuiet
            ? []
            : _entries.OrderBy(e => e.Card.AutoClose is not null).ThenByDescending(e => e.Sequence).Take(MaxVisible).ToList();
        foreach (var entry in _entries)
        {
            if (entry.Card.AutoClose is not { } autoClose || !shown.Contains(entry))
            {
                entry.ExpiresAt = null;
                entry.Remaining = null;
            }
            else if (entry.ExpiresAt is null && entry.Remaining is null)
            {
                if (_hovering) entry.Remaining = autoClose;
                else entry.ExpiresAt = now + autoClose;
            }
        }
        _visible = shown.OrderByDescending(e => e.Sequence).Select(e => new BoardEntry(e.Card, e.ShownAt, e.ExpiresAt, e.Remaining)).ToList();
    }

    private sealed class Entry(NotificationCard card)
    {
        public NotificationCard Card { get; set; } = card;
        public DateTimeOffset ShownAt { get; set; }
        // Order of arrival: breaks the ties of cards shown at the same instant.
        public long Sequence { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public TimeSpan? Remaining { get; set; }
        // Queued while quiet by a card whose sound is Attention: one Attention plays when the quiet ends.
        public bool SoundOwed { get; set; }
    }
}
