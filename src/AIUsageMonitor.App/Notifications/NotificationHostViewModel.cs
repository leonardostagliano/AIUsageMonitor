using System.Collections.ObjectModel;
using System.Windows;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.Core.Notifications;
using AIUsageMonitor.Core.Presentation;

namespace AIUsageMonitor.App.Notifications;

/// <summary>
/// La pila visibile delle notifiche (spec 2026-09-27 §5.4, lato UI). Non decide nulla: <see cref="Sync"/> ricopia
/// l'istantanea del board (<see cref="NotificationBoard.Visible"/>, <see cref="NotificationBoard.HiddenCount"/>) e
/// clic, ✕, pillola e passaggio del mouse tornano al servizio come eventi. Le card uscite dal board restano in
/// <see cref="Cards"/> con <see cref="NotificationCardViewModel.IsLeaving"/> finche' la finestra non ha finito
/// l'animazione d'uscita e chiama <see cref="CompleteExit"/>. Tutto sul thread UI.
/// </summary>
public sealed class NotificationHostViewModel : ObservableObject
{
    private int _hiddenCount;
    private bool _pointerOver;
    private bool _shown;
    private bool _isHovering;

    /// <summary>Le card nell'ordine della pila, dall'alto: quelle del board piu' quelle che stanno uscendo.</summary>
    public ObservableCollection<NotificationCardViewModel> Cards { get; } = new();

    public int HiddenCount
    {
        get => _hiddenCount;
        private set
        {
            if (!Set(ref _hiddenCount, value)) return;
            Raise(nameof(MoreText));
            Raise(nameof(MoreVisibility));
        }
    }

    /// <summary>"+1 altra", "+3 altre": card in attesa di un posto nella pila.</summary>
    public string MoreText => NotificationPresentation.MoreText(HiddenCount);
    public Visibility MoreVisibility => HiddenCount > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Il mouse e' sopra la pila e i timer sono fermi: vale solo a finestra mostrata e con almeno una card viva
    /// (<see cref="NotificationPresentation.StackHover"/>). Il board resta fermo finche' non riceve
    /// <c>SetHover(false)</c> e WPF puo' non mandare MouseLeave a una finestra nascosta sotto il mouse, quindi nascondere
    /// la finestra o svuotare la pila chiude il passaggio del mouse da solo.
    /// </summary>
    public bool IsHovering => _isHovering;

    /// <summary>Clic sul corpo di una card (non su una card che sta uscendo).</summary>
    public event Action<NotificationCardViewModel>? Activated;

    /// <summary>Clic sulla ✕ di una card (non su una card che sta uscendo).</summary>
    public event Action<NotificationCardViewModel>? Dismissed;

    /// <summary>Clic sulla pillola "+N altre".</summary>
    public event Action? MoreActivated;

    /// <summary>Il mouse entra (true) o esce (false) dalla pila; alzato solo quando <see cref="IsHovering"/> cambia.</summary>
    public event Action<bool>? HoverChanged;

    /// <summary>
    /// Riconcilia la pila con l'istantanea del board per <see cref="NotificationCard.Key"/>: una chiave gia' presente
    /// aggiorna la sua card sul posto (nessuna nuova entrata), una nuova crea la card, una sparita la marca
    /// <see cref="NotificationCardViewModel.IsLeaving"/>. L'ordine e' quello di <see cref="NotificationPresentation.Arrange"/>.
    /// Aggiorna anche eta' e timer: il servizio la richiama a ogni tick.
    /// </summary>
    public void Sync(IReadOnlyList<BoardEntry> visible, int hiddenCount, DateTimeOffset now)
    {
        var byKey = Cards.ToDictionary(card => card.Key, StringComparer.Ordinal);
        foreach (var entry in visible)
        {
            if (byKey.TryGetValue(entry.Card.Key, out var card)) card.Update(entry, now);
            else byKey[entry.Card.Key] = new NotificationCardViewModel(entry, now);
        }

        var slots = NotificationPresentation.Arrange(
            Cards.Select(card => new StackSlot(card.Key, card.IsLeaving)).ToList(),
            visible.Select(entry => entry.Card.Key).ToList());
        for (var i = 0; i < slots.Count; i++)
        {
            var card = byKey[slots[i].Key];
            var at = Cards.IndexOf(card);
            if (at == i) continue;
            if (at < 0) Cards.Insert(i, card);
            else Cards.Move(at, i);
        }
        // Solo a pila ordinata: con la finestra nascosta una card marcata in uscita viene tolta subito
        // (CompleteExit), e toglierla dentro il ciclo qui sopra ne sposterebbe gli indici.
        foreach (var slot in slots) byKey[slot.Key].IsLeaving = slot.Leaving;
        HiddenCount = hiddenCount;
        // Per ultimo: chi ascolta HoverChanged puo' richiamare Sync e deve trovare la pila gia' allineata.
        UpdateHover();
    }

    /// <summary>Fine dell'animazione d'uscita: toglie la card, se nel frattempo il board non l'ha fatta tornare.</summary>
    public void CompleteExit(NotificationCardViewModel card)
    {
        if (card.IsLeaving) Cards.Remove(card);
    }

    public void Activate(NotificationCardViewModel card)
    {
        if (!card.IsLeaving) Activated?.Invoke(card);
    }

    public void Dismiss(NotificationCardViewModel card)
    {
        if (!card.IsLeaving) Dismissed?.Invoke(card);
    }

    public void ActivateMore() => MoreActivated?.Invoke();

    /// <summary>Il mouse entra (true) o esce (false) dalla pila: lo dice la finestra.</summary>
    public void SetHover(bool hovering)
    {
        _pointerOver = hovering;
        UpdateHover();
    }

    /// <summary>
    /// La finestra si e' mostrata o nascosta. Nascosta, il mouse non e' piu' sopra la pila anche se WPF non manda
    /// MouseLeave: il passaggio del mouse finisce e alla prossima comparsa riparte solo da un nuovo MouseEnter.
    /// </summary>
    internal void SetShown(bool shown)
    {
        _shown = shown;
        if (!shown) _pointerOver = false;
        UpdateHover();
    }

    private void UpdateHover()
    {
        var hovering = NotificationPresentation.StackHover(_pointerOver, _shown, Cards.Count(card => !card.IsLeaving));
        if (_isHovering == hovering) return;
        _isHovering = hovering;
        Raise(nameof(IsHovering));
        HoverChanged?.Invoke(hovering);
    }
}
