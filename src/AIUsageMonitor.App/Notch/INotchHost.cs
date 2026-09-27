using AIUsageMonitor.Core.Notch;

namespace AIUsageMonitor.App.Notch;

public interface INotchHost
{
    bool IsNotchVisible { get; }

    /// <summary>
    /// Dove sta il notch per la pila delle notifiche (spec 2026-09-27 §6): area di lavoro e scala del suo monitor, centro
    /// della linguetta in pixel fisici e quanto occupa sul bordo destro (linguetta, pannello aperto, 0 se nascosto).
    /// Si legge sul thread UI.
    /// </summary>
    NotchAnchor Anchor { get; }

    /// <summary>
    /// Alzato sul thread UI quando <see cref="Anchor"/> puo' essere cambiato: dopo ogni riposizionamento, all'apertura e
    /// alla chiusura del pannello, quando il notch viene mostrato o nascosto e al cambio della modalita' compatta.
    /// </summary>
    event Action? AnchorChanged;

    void Pin();
    void TogglePin();
    void ToggleVisible();
}
