using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Notifications;

namespace AIUsageMonitor.Core.Presentation;

/// <summary>One slot of the notification stack: a card key and whether the card is on its way out (exit animation).</summary>
public readonly record struct StackSlot(string Key, bool Leaving);

/// <summary>
/// Presentation rules of the notification cards (spec 2026-09-27 §5.2), kept out of the view models so they can be
/// tested without WPF: the texts, the theme keys the cards look up and the order of the stack.
/// </summary>
public static class NotificationPresentation
{
    /// <summary>How long ago a card appeared: "ora" under a minute, then "2 min", "1 h", and "3 g" past a day.</summary>
    public static string AgeText(DateTimeOffset shownAt, DateTimeOffset now)
    {
        var elapsed = now - shownAt;
        if (elapsed < TimeSpan.FromMinutes(1)) return "ora";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes} min";
        if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours} h";
        return $"{(int)elapsed.TotalDays} g";
    }

    /// <summary>The pill under the stack: "+1 altra", "+3 altre"; empty when no card is waiting for a slot.</summary>
    public static string MoreText(int hiddenCount) =>
        hiddenCount <= 0 ? "" : hiddenCount == 1 ? "+1 altra" : $"+{hiddenCount} altre";

    /// <summary>Theme brush of the dot and the label of row 2; every one meets 4.5:1 on Card (ThemePaletteTests).</summary>
    public static string ToneKey(NotificationTone tone) => tone switch
    {
        NotificationTone.Success => "SuccessText",
        NotificationTone.Warning => "WarningText",
        NotificationTone.Question => "Focus",
        NotificationTone.Danger => "DangerText",
        _ => "TextMuted"
    };

    /// <summary>
    /// Theme brush of the timer bar of a card that closes by itself: the normal tone gradient for "Finito",
    /// TextDisabled for everything else (app notices); null for a card that stays.
    /// </summary>
    public static string? TimerBrushKey(NotificationCard card) =>
        card.AutoClose is null ? null : card.Kind == NotificationKind.Finished ? "ToneNormalBrush" : "TextDisabled";

    /// <summary>Glow in the top-left corner of the card; null for app notices (no agent).</summary>
    public static string? GlowKey(AgentKind? agent) => agent switch
    {
        AgentKind.Claude => "ClaudeGlow",
        AgentKind.Codex => "CodexGlow",
        _ => null
    };

    /// <summary>Fill of the avatar circle, as on the notch cards; null for app notices (they show the app logo).</summary>
    public static string? BrandKey(AgentKind? agent) => agent switch
    {
        AgentKind.Claude => "BrandClaude",
        AgentKind.Codex => "BrandCodex",
        _ => null
    };

    /// <summary>Agent glyph drawn in the avatar circle (Icons.xaml); null for app notices.</summary>
    public static string? IconKey(AgentKind? agent) => agent switch
    {
        AgentKind.Claude => "ClaudeIcon",
        AgentKind.Codex => "CodexIcon",
        _ => null
    };

    /// <summary>
    /// Time left before an auto-close card closes: the board's <see cref="BoardEntry.Remaining"/> when it gives one
    /// (it stops moving while the mouse is over the stack), otherwise <see cref="BoardEntry.ExpiresAt"/> minus
    /// <paramref name="now"/>, otherwise the whole AutoClose; never negative. Null for a card that stays.
    /// </summary>
    public static TimeSpan? TimerRemaining(BoardEntry entry, DateTimeOffset now)
    {
        if (entry.Card.AutoClose is not { } total) return null;
        var left = entry.Remaining ?? (entry.ExpiresAt is { } expires ? expires - now : total);
        return left < TimeSpan.Zero ? TimeSpan.Zero : left > total ? total : left;
    }

    /// <summary>The part of the timer bar still full, from 1 (just shown) to 0 (closing); 0 for a card that stays.</summary>
    public static double TimerFraction(BoardEntry entry, DateTimeOffset now)
    {
        if (entry.Card.AutoClose is not { } total || total <= TimeSpan.Zero) return 0;
        return TimerRemaining(entry, now)!.Value / total;
    }

    /// <summary>
    /// The stack after a board snapshot: the visible keys in board order (newest first, persistent cards first), and
    /// every other current card kept as leaving right after the visible card that preceded it, so a card on its way out
    /// never jumps while it fades. A leaving key the board shows again is live again; a card is removed from the stack
    /// only when its exit animation ends, not here.
    /// </summary>
    public static IReadOnlyList<StackSlot> Arrange(IReadOnlyList<StackSlot> current, IReadOnlyList<string> visibleKeys)
    {
        var result = visibleKeys.Select(key => new StackSlot(key, false)).ToList();
        var wanted = new HashSet<string>(visibleKeys, StringComparer.Ordinal);
        var insertAt = 0;
        foreach (var slot in current)
        {
            if (wanted.Contains(slot.Key))
            {
                insertAt = result.FindIndex(s => !s.Leaving && s.Key == slot.Key) + 1;
                continue;
            }
            result.Insert(insertAt++, slot with { Leaving = true });
        }
        return result;
    }

    /// <summary>
    /// Whether the stack counts as hovered, the state that freezes the board's timers: the mouse is over it, the host
    /// window is shown and at least one card is live (not on its way out). The board keeps its timers frozen until it
    /// is told otherwise, and WPF may never send MouseLeave to a window hidden under the mouse, so hiding the window or
    /// emptying the stack ends the hover by itself.
    /// </summary>
    public static bool StackHover(bool pointerOver, bool windowShown, int liveCards) =>
        pointerOver && windowShown && liveCards > 0;
}
