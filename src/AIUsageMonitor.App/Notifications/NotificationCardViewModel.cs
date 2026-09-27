using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Tray;
using AIUsageMonitor.Core.Notifications;
using AIUsageMonitor.Core.Presentation;

namespace AIUsageMonitor.App.Notifications;

/// <summary>
/// Una card della pila delle notifiche (spec 2026-09-27 §5.2). La crea e la aggiorna
/// <see cref="NotificationHostViewModel.Sync"/>: la chiave resta la stessa per tutta la vita della card, il contenuto
/// cambia sul posto quando il board sostituisce la card della stessa sessione. Testi e chiavi dei pennelli vengono da
/// <see cref="NotificationPresentation"/>; qui si risolvono solo i pennelli di Theme.xaml.
/// </summary>
public sealed class NotificationCardViewModel : ObservableObject
{
    private static readonly Lazy<ImageSource?> AppLogo = new(RenderAppLogo);

    private NotificationCard _card;
    private string _ageText = "";
    private double _timerFraction;
    private bool _isLeaving;
    private Brush _toneBrush = Brushes.Transparent;
    private Brush _glowBrush = Brushes.Transparent;
    private Brush? _brandBrush;
    private Brush? _timerBrush;
    private Geometry? _icon;

    public NotificationCardViewModel(BoardEntry entry, DateTimeOffset now)
    {
        Key = entry.Card.Key;
        _card = entry.Card;
        ApplyCard();
        Update(entry, now);
    }

    /// <summary>Chiave del board (<see cref="NotificationCard.Key"/>): identifica la card nella riconciliazione.</summary>
    public string Key { get; }

    public NotificationCard Card => _card;
    public string Title => _card.Title;
    public string Label => _card.Label;
    public string Message => _card.Message;

    /// <summary>
    /// La riga 3: il messaggio su una riga sola (una TextBlock disegna i suoi a capo anche senza wrapping); il testo
    /// intero resta nel tooltip (<see cref="Message"/>).
    /// </summary>
    public string MessageLine => NotificationPresentation.OneLine(_card.Message);

    public string AgeText { get => _ageText; private set => Set(ref _ageText, value); }

    /// <summary>Pallino ed etichetta della riga 2 (SuccessText, WarningText, Focus, DangerText, TextMuted).</summary>
    public Brush ToneBrush { get => _toneBrush; private set => Set(ref _toneBrush, value); }

    /// <summary>Alone dell'agente nell'angolo in alto a sinistra; trasparente per gli avvisi dell'app.</summary>
    public Brush GlowBrush { get => _glowBrush; private set => Set(ref _glowBrush, value); }

    // Avatar: il cerchio dell'agente come nella card del notch, oppure il logo dell'app per gli avvisi.
    public Brush? BrandBrush { get => _brandBrush; private set => Set(ref _brandBrush, value); }
    public Geometry? Icon { get => _icon; private set => Set(ref _icon, value); }
    public ImageSource? Logo => _card.Agent is null ? AppLogo.Value : null;
    public Visibility AgentVisibility => _card.Agent is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility LogoVisibility => _card.Agent is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Vero per le card che si chiudono da sole (Finito, avvisi Info): hanno la barra del timer.</summary>
    public bool HasTimer => _card.AutoClose is not null;
    public Visibility TimerVisibility => HasTimer ? Visibility.Visible : Visibility.Collapsed;
    public Brush? TimerBrush { get => _timerBrush; private set => Set(ref _timerBrush, value); }

    /// <summary>
    /// Parte della barra ancora piena (1 appena comparsa, 0 alla chiusura) all'istante dell'ultimo
    /// <see cref="Update"/>. La finestra la usa come punto di partenza e anima fino a zero da sola (vedi
    /// <see cref="NotificationHostWindow"/>): basta aggiornarla a ogni tick del servizio.
    /// </summary>
    public double TimerFraction { get => _timerFraction; private set => Set(ref _timerFraction, value); }

    /// <summary>La card e' uscita dal board e sta facendo l'animazione d'uscita; la toglie <see cref="NotificationHostViewModel.CompleteExit"/>.</summary>
    public bool IsLeaving { get => _isLeaving; internal set => Set(ref _isLeaving, value); }

    /// <summary>Nuovo contenuto (card sostituita sul posto), eta' e timer all'istante <paramref name="now"/>.</summary>
    public void Update(BoardEntry entry, DateTimeOffset now)
    {
        if (!Equals(entry.Card, _card))
        {
            _card = entry.Card;
            ApplyCard();
        }
        AgeText = NotificationPresentation.AgeText(entry.ShownAt, now);
        TimerFraction = NotificationPresentation.TimerFraction(entry, now);
    }

    private void ApplyCard()
    {
        ToneBrush = ThemeBrush(NotificationPresentation.ToneKey(_card.Tone)) ?? Brushes.Gray;
        GlowBrush = ThemeBrush(NotificationPresentation.GlowKey(_card.Agent)) ?? Brushes.Transparent;
        BrandBrush = ThemeBrush(NotificationPresentation.BrandKey(_card.Agent));
        Icon = NotificationPresentation.IconKey(_card.Agent) is { } icon ? Application.Current?.TryFindResource(icon) as Geometry : null;
        TimerBrush = ThemeBrush(NotificationPresentation.TimerBrushKey(_card));
        foreach (var name in new[] { nameof(Card), nameof(Title), nameof(Label), nameof(Message), nameof(MessageLine), nameof(Logo),
                     nameof(AgentVisibility), nameof(LogoVisibility), nameof(HasTimer), nameof(TimerVisibility) })
            Raise(name);
    }

    private static Brush? ThemeBrush(string? key) =>
        key is null ? null : Application.Current?.TryFindResource(key) as Brush;

    /// <summary>
    /// Il logo dell'app (lo stesso disegno dell'icona della tray, senza pallino) a 96 px, abbastanza per 30 DIP fino
    /// al 300 %. Congelato e condiviso da tutte le card; null se il rendering fallisce (la card resta senza avatar).
    /// </summary>
    private static ImageSource? RenderAppLogo()
    {
        try
        {
            using var rendered = TrayIconRenderer.Render(null, 96);
            var source = Imaging.CreateBitmapSourceFromHIcon(rendered.Icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
