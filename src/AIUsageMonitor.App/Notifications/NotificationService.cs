using System.Windows.Threading;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Notch;
using AIUsageMonitor.App.Startup;
using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Notifications;
using AIUsageMonitor.Core.Settings;

namespace AIUsageMonitor.App.Notifications;

/// <summary>
/// Le notifiche dell'app (spec 2026-09-27): porta i cambi delle sessioni e gli avvisi dell'app nel
/// <see cref="NotificationBoard"/> e il board nella pila di card di <see cref="NotificationHostWindow"/>, con suono e
/// silenzio. Board e finestra vivono solo sul thread UI: i cambi delle sessioni arrivano sul thread della pump, dove
/// si compone l'intento (puro, nessuna IO), e passano al thread UI con <see cref="UiDispatcher"/>. Un timer da 1 s gira
/// solo finche' c'e' almeno una card (visibile, in attesa o in coda): scadenze, eta' e barre dei timer, e ogni 2 s il
/// probe di "Non disturbare". Nessun errore delle notifiche ferma il monitoraggio: nel log finisce solo il tipo di
/// errore, mai nomi di sessione, messaggi o comandi.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan QuietPollInterval = TimeSpan.FromSeconds(2);

    private readonly AppServices _services;
    private readonly INotchHost _notch;
    private readonly Action _openUpdatePrompt;
    private readonly NotificationBoard _board;
    private readonly NotificationHostViewModel _viewModel;
    private readonly NotificationHostWindow _window;
    private readonly DispatcherTimer _timer;
    private readonly Action<SessionChange> _sessionChanged;
    private readonly Action<string, string, NoticeKind> _notice;
    private readonly Action<string> _quietLog;
    private readonly Action<string> _soundLog;
    private readonly List<DispatcherTimer> _demoTimers = new();
    private DateTimeOffset _lastQuietProbe = DateTimeOffset.MinValue;
    private bool _demoEndsWhenEmpty;
    private bool _disposed;

    /// <summary>
    /// Va creato sul thread UI dopo <c>AppServices.Start()</c>: il replay silenzioso della pump e' gia' finito e la
    /// cronologia non genera card. Da qui in poi e' l'unico renderer di <see cref="AppServices.Notice"/>.
    /// </summary>
    public NotificationService(AppServices services, INotchHost notch, Action openUpdatePrompt)
    {
        _services = services;
        _notch = notch;
        _openUpdatePrompt = openUpdatePrompt;
        _board = new NotificationBoard(services.Clock);
        _viewModel = new NotificationHostViewModel();
        _window = new NotificationHostWindow(_viewModel, notch);
        _viewModel.Activated += OnActivated;
        _viewModel.Dismissed += card => Safely("chiusura di una card", () => Update(_board.Dismiss(card.Key)));
        _viewModel.MoreActivated += () => Safely("apertura del notch", notch.Pin);
        // Anche il false che la pila alza da sola quando la finestra si nasconde o si svuota: senza, il board terrebbe
        // fermi i timer delle card che arrivano dopo.
        _viewModel.HoverChanged += hovering => Safely("pausa dei timer", () => Update(_board.SetHover(hovering)));
        _timer = new DispatcherTimer { Interval = TickInterval };
        _timer.Tick += (_, _) => Safely("aggiornamento periodico", OnTick);
        _quietLog = message => services.Log.Warn($"Notifiche: silenzio non leggibile ({message}), le card si mostrano sempre");
        _soundLog = message => services.Log.Warn($"Notifiche: suono non riprodotto ({message}), le card restano mute");
        _sessionChanged = OnSessionChanged;
        _notice = (title, text, kind) => ShowNotice(title, text, kind);
        services.Sessions.Changed += _sessionChanged;
        services.Notice += _notice;
    }

    /// <summary>
    /// Card di un avviso dell'app (aggiornamento, hook, terminale non trovato...). Si puo' chiamare da qualunque thread:
    /// la card passa dal thread UI. <paramref name="action"/> e' cio' che fa il clic: di default fissa il notch,
    /// <see cref="NotificationAction.OpenUpdate"/> apre la conferma di aggiornamento. Gli avvisi sono muti.
    /// </summary>
    public void ShowNotice(string title, string text, NoticeKind kind, NotificationAction action = NotificationAction.PinNotch)
    {
        var card = NotificationComposer.Notice(title, text, SeverityOf(kind), action);
        UiDispatcher.Post(() => Safely("avviso", () => Apply(new ShowIntent(card))));
    }

    /// <summary>
    /// <c>--test-notification</c> (spec §9): le card di <see cref="NotificationDemo"/> in due ondate, piu' un avviso
    /// informativo, con le impostazioni di default (ogni tipo, ogni agente) qualunque siano quelle dell'utente;
    /// l'interruttore "Suono" invece vale anche qui. L'app si chiude quando, dopo l'ultima card d'esempio, non resta
    /// nessuna card, o comunque dopo 60 s. Sul thread UI.
    /// </summary>
    public void ShowDemo()
    {
        Safely("dimostrazione", () =>
        {
            var now = _services.Clock.UtcNow;
            ShowNotice(NotificationDemo.NoticeTitle, NotificationDemo.NoticeText, NoticeKind.Info);
            ScheduleDemoWave(NotificationDemo.FirstWave(now), TimeSpan.Zero, endsDemo: false);
            ScheduleDemoWave(NotificationDemo.SecondWave(now), NotificationDemo.SecondWaveDelay, endsDemo: true);
            After(NotificationDemo.MaxDuration, QuitDemo);
        });
    }

    /// <summary>Thread della pump: qui si compone soltanto; il board si tocca sul thread UI.</summary>
    private void OnSessionChanged(SessionChange change)
    {
        NotificationIntent? intent;
        try
        {
            intent = NotificationComposer.Compose(change, _services.Settings.Current);
        }
        catch (Exception ex)
        {
            _services.Log.Error($"Notifiche (composizione): {ex.GetType().Name}");
            return;
        }
        if (intent is not null) UiDispatcher.Post(() => Safely("cambio di sessione", () => Apply(intent)));
    }

    /// <summary>
    /// Una card nuova rilegge subito il silenzio (spec §7): in "Non disturbare" va in coda invece di comparire. Se il
    /// silenzio e' appena finito, il suo suono e quello della card sono un passo solo: suona il piu' forte (spec §8).
    /// </summary>
    private void Apply(NotificationIntent intent)
    {
        var quiet = intent is ShowIntent ? ProbeQuiet() : default;
        var applied = _board.Apply(intent);
        Update(new BoardResult(quiet.Changed || applied.Changed, Stronger(quiet.Sound, applied.Sound)));
    }

    private BoardResult ProbeQuiet()
    {
        _lastQuietProbe = _services.Clock.UtcNow;
        return _board.SetQuiet(QuietModeProbe.IsQuiet(_quietLog));
    }

    private void OnTick()
    {
        var quiet = _services.Clock.UtcNow - _lastQuietProbe >= QuietPollInterval ? ProbeQuiet() : default;
        var ticked = _board.Tick();
        Update(new BoardResult(quiet.Changed || ticked.Changed, Stronger(quiet.Sound, ticked.Sound)));
    }

    /// <summary>
    /// Esito di un passo del board: suono, pila, timer e fine della dimostrazione. La pila si riallinea a ogni passo,
    /// anche senza cambi: cosi' il tick aggiorna eta' e barre dei timer e la pausa al passaggio del mouse ferma le barre
    /// dove sono. Il board non chiede suoni in silenzio; "Suono" si rilegge qui perche' una card rimasta in coda durante
    /// il silenzio e' stata composta quando l'interruttore poteva essere ancora acceso.
    /// </summary>
    private void Update(BoardResult result)
    {
        if (result.Sound != NotificationSoundKind.None && !_board.IsQuiet && _services.Settings.Current.NotifySound)
            NotificationSound.Play(result.Sound, _soundLog);
        _viewModel.Sync(_board.Visible, _board.HiddenCount, _services.Clock.UtcNow);
        _window.Refresh();
        if (_board.IsEmpty) _timer.Stop();
        else if (!_timer.IsEnabled) _timer.Start();
        EndDemoIfDone();
    }

    /// <summary><see cref="NotificationSoundKind"/> va dal piu' debole al piu' forte: Attention prevale su Done.</summary>
    private static NotificationSoundKind Stronger(NotificationSoundKind a, NotificationSoundKind b) =>
        (NotificationSoundKind)Math.Max((int)a, (int)b);

    /// <summary>
    /// Clic sul corpo di una card (spec §5.3): una sessione va al suo terminale, pagina cloud o app come il nome della
    /// riga nel notch; "aggiornamento disponibile" apre la conferma; gli altri avvisi fissano il notch. Poi la card si
    /// chiude, e lo stato che l'ha generata non la riapre finche' la sessione non cambia. La sessione e' quella di
    /// adesso (<see cref="SessionCardTarget"/>): la card tiene l'istantanea di quando e' stata composta, che puo' avere
    /// un nome o un terminale ormai superati; l'istantanea serve solo se la sessione non esiste piu'.
    /// </summary>
    private void OnActivated(NotificationCardViewModel card)
    {
        if (_disposed) return;
        var target = card.Card;
        try
        {
            switch (target.Action)
            {
                case NotificationAction.FocusSession when SessionCardTarget.Resolve(target, _services.Sessions.Sessions) is { } session:
                    _ = FocusAsync(session);
                    break;
                case NotificationAction.OpenUpdate:
                    _openUpdatePrompt();
                    break;
                default:
                    _notch.Pin();
                    break;
            }
        }
        catch (Exception ex)
        {
            _services.Log.Error($"Notifiche (azione {target.Action}): {ex.GetType().Name}");
        }
        Safely("chiusura di una card", () => Update(_board.Dismiss(target.Key)));
    }

    /// <summary>
    /// Come <c>SessionRowViewModel.FocusAsync</c>: <c>FocusTerminalAsync</c> gira su un thread di background e non
    /// solleva; se nessuna strategia funziona compare l'avviso "Terminale non trovato" o "Pagina della sessione non
    /// aperta". La sessione e' quella che il clic ha scelto.
    /// </summary>
    private async Task FocusAsync(SessionState session)
    {
        bool focused;
        try
        {
            focused = await _services.FocusTerminalAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _services.Log.Error($"Focus {session.Agent} {session.SessionId}: {ex.GetType().Name}");
            focused = false;
        }
        if (!focused) ShowNotice(FocusFailureNotice.Title(session), FocusFailureNotice.Text(session), NoticeKind.Warning);
    }

    /// <summary>
    /// Le card di un'ondata arrivano a <see cref="NotificationDemo.CardInterval"/> l'una dall'altra, cosi' il suono di
    /// ciascuna si sente per intero (Windows ne suona uno per processo: il nuovo interrompe il precedente). Dopo l'ultima
    /// card della dimostrazione l'app aspetta che la pila si svuoti.
    /// </summary>
    private void ScheduleDemoWave(IReadOnlyList<SessionChange> changes, TimeSpan start, bool endsDemo)
    {
        var settings = new AppSettings();
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            var last = endsDemo && i == changes.Count - 1;
            After(start + i * NotificationDemo.CardInterval, () =>
            {
                if (NotificationComposer.Compose(change, settings) is { } intent) Apply(intent);
                if (!last) return;
                _demoEndsWhenEmpty = true;
                EndDemoIfDone();
            });
        }
    }

    /// <summary>Con un ritardo nullo esegue subito, altrimenti con un timer del thread UI (fermato da Dispose).</summary>
    private void After(TimeSpan delay, Action action)
    {
        if (delay <= TimeSpan.Zero)
        {
            Safely("dimostrazione", action);
            return;
        }
        var timer = new DispatcherTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Safely("dimostrazione", action);
        };
        _demoTimers.Add(timer);
        timer.Start();
    }

    private void EndDemoIfDone()
    {
        if (_demoEndsWhenEmpty && _board.IsEmpty) QuitDemo();
    }

    private void QuitDemo()
    {
        _demoEndsWhenEmpty = false;
        System.Windows.Application.Current?.Shutdown();
    }

    private static NoticeSeverity SeverityOf(NoticeKind kind) => kind switch
    {
        NoticeKind.Error => NoticeSeverity.Error,
        NoticeKind.Warning => NoticeSeverity.Warning,
        _ => NoticeSeverity.Info
    };

    private void Safely(string what, Action action)
    {
        if (_disposed) return;
        try { action(); }
        catch (Exception ex) { _services.Log.Error($"Notifiche ({what}): {ex.GetType().Name}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _services.Sessions.Changed -= _sessionChanged;
        _services.Notice -= _notice;
        _timer.Stop();
        foreach (var timer in _demoTimers) timer.Stop();
        _window.Close();
    }
}
