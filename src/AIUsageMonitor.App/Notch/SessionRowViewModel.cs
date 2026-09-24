using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Startup;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Presentation;
using AIUsageMonitor.Core.Pricing;

namespace AIUsageMonitor.App.Notch;

public sealed class SessionRowViewModel : ObservableObject
{
    /// <summary>
    /// Which session rows are expanded, in memory only and keyed by session id: a row is rebuilt whenever the
    /// session list shrinks and grows again, so the flag cannot live on the instance alone. Only the expanded ids
    /// are kept (collapsing removes the entry), so the set stays as small as what the user has open.
    /// </summary>
    private static readonly HashSet<string> ExpandedSessionIds = new(StringComparer.Ordinal);

    /// <summary>Prima riga del tooltip quando la riga porta al terminale; il resto e' il testo cwd/messaggio di sempre.</summary>
    private const string FocusHint = "Porta in primo piano il terminale";

    private readonly AppServices _services;
    private readonly RelayCommand _focusTerminal;
    private SessionState _session;
    private PricingSnapshot? _pricing;
    private string _subtitle = "";

    public SessionRowViewModel(SessionState session, AppServices services, DateTimeOffset now, PricingSnapshot? pricing)
    {
        _session = session;
        _services = services;
        ToggleCommand = new RelayCommand(Toggle);
        _focusTerminal = new RelayCommand(FocusTerminal, () => CanFocus);
        _pricing = pricing;
        SyncSubagents(now);
        Tick(now);
    }

    public string SessionId => _session.SessionId;
    public string Name => _session.DisplayName;
    public string PhaseLabel => _session.PhaseLabel;

    /// <summary>Lettera dell'avatar (spec 6.3): prima lettera o cifra del nome, "•" altrimenti.</summary>
    public string Initial => NotchPresentation.Initial(_session.DisplayName);

    private PhaseTone Tone => NotchPresentation.ToneOf(_session.Phase);
    public Brush ToneBrush => PhaseVisuals.ToneBrush(Tone);
    public Brush ToneFill => PhaseVisuals.ToneFill(Tone);
    public Brush ToneTextBrush => PhaseVisuals.ToneText(Tone);

    public bool IsPulsing => _session.Phase == SessionPhase.Working;

    public string Tooltip
    {
        get
        {
            var detail = string.Join("\n", new[] { _session.Cwd, _session.Message }.Where(s => !string.IsNullOrEmpty(s)));
            if (!CanFocus) return detail;
            return detail.Length == 0 ? FocusHint : FocusHint + "\n" + detail;
        }
    }

    /// <summary>"al lavoro · 2 agenti · 1m": aggiornato a ogni tick come il tempo trascorso.</summary>
    public string Subtitle { get => _subtitle; private set => Set(ref _subtitle, value); }

    /// <summary>
    /// Porta in primo piano il terminale della sessione (click sul nome). Vero solo quando l'evento della sessione ha
    /// portato un host: senza di esso non c'e' nulla da risolvere e la riga resta una semplice etichetta.
    /// </summary>
    public bool CanFocus => _session.Host is not null;

    /// <summary>Mano solo quando il click fa qualcosa; il template la lega al <c>TextBlock</c> del nome.</summary>
    public Cursor NameCursor => CanFocus ? Cursors.Hand : Cursors.Arrow;

    public ICommand FocusTerminalCommand => _focusTerminal;

    /// <summary>Cost of the tokens on this row — the session itself, not its agents, like the token counts; null while costs are hidden.</summary>
    private CostResult? OwnCost => _pricing?.Cost(_session.Ledger);

    /// <summary>Costo dei soli token della riga, testo breve di oggi; null con i costi nascosti.</summary>
    public string? CostText => OwnCost is { } cost ? CostFormatter.Short(cost) : null;
    public Visibility CostVisibility => CostText is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Processed input and output of the conversation, including input served from cache.</summary>
    public string TokensShort => _session.Tokens is { } tokens ? TokenFormatter.InputOutput(tokens) : "Token in attesa";

    /// <summary>Breakdown for the tooltip of the token column (plus the cost block); null (no tooltip) when there is no total yet.</summary>
    public string? TokensTooltip
    {
        get
        {
            if (_session.Tokens is not { Total: > 0 } tokens) return null;
            var breakdown = TokenFormatter.Breakdown(tokens);
            if (_pricing is null || OwnCost is not { } own) return breakdown;
            var withSubagents = _session.SubagentLedger.IsEmpty
                ? null
                : _pricing.Cost((_session.Ledger ?? UsageLedger.Empty) + _session.SubagentLedger);
            return CostTooltips.Row(own, withSubagents, _pricing) is { } block ? $"{breakdown}\n\n{block}" : breakdown;
        }
    }

    /// <summary>Running subagents, most recently started first.</summary>
    public ObservableCollection<SubagentRowViewModel> Subagents { get; } = new();

    public bool HasSubagents => _session.ActiveSubagents > 0;

    /// <summary>Counts and totals describe only the active workflow rows.</summary>
    public string SubagentSummary
    {
        get
        {
            var count = _session.ActiveSubagents;
            if (count == 0) return "";
            var label = count == 1 ? "1 agente attivo" : $"{count} agenti attivi";
            var summary = $"{label} · {TokenFormatter.Compact(_session.ActiveSubagentTokens.Total)} tok";
            return _pricing?.Cost(_session.ActiveSubagentLedger) is { } cost && CostFormatter.Short(cost) is { } costText
                ? $"{summary} · {costText}"
                : summary;
        }
    }

    public ICommand ToggleCommand { get; }

    public bool IsExpanded => ExpandedSessionIds.Contains(SessionId);

    public string ChevronGlyph => IsExpanded ? "▾" : "▸";

    public Visibility SummaryVisibility => HasSubagents ? Visibility.Visible : Visibility.Collapsed;

    public Visibility SubagentListVisibility => HasSubagents && IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    public void Update(SessionState session, DateTimeOffset now, PricingSnapshot? pricing)
    {
        _session = session;
        _pricing = pricing;
        Raise(nameof(Name)); Raise(nameof(PhaseLabel));
        Raise(nameof(Initial)); Raise(nameof(ToneBrush)); Raise(nameof(ToneFill)); Raise(nameof(ToneTextBrush));
        Raise(nameof(IsPulsing)); Raise(nameof(Tooltip));
        Raise(nameof(CostText)); Raise(nameof(CostVisibility)); Raise(nameof(TokensShort)); Raise(nameof(TokensTooltip));
        // L'host arriva col primo SessionStart/UserPromptSubmit: una riga nata senza puo' diventare cliccabile dopo.
        Raise(nameof(CanFocus)); Raise(nameof(NameCursor));
        _focusTerminal.RaiseCanExecuteChanged();
        RaiseSubagentState();
        SyncSubagents(now);
        Tick(now);
    }

    public void Tick(DateTimeOffset now)
    {
        Subtitle = NotchPresentation.SessionSubtitle(_session, now);
        foreach (var subagent in Subagents) subagent.Tick(now);
    }

    /// <summary>
    /// Click sul nome della riga. <c>FocusTerminalAsync</c> gira gia' tutto su un thread di background (CLI di Herdr e
    /// risalita dei processi, 3 s di timeout) e non solleva, quindi qui basta non aspettarlo: il thread della UI torna
    /// subito e il notch resta reattivo. Il toast di fallimento torna sul thread della UI perche' lo mostra la tray.
    /// La sessione viene catturata adesso: la riga puo' essere aggiornata mentre la catena delle strategie e' in corso.
    /// </summary>
    private void FocusTerminal()
    {
        if (!CanFocus) return;
        var session = _session;
        _ = FocusAsync(session);
    }

    private async Task FocusAsync(SessionState session)
    {
        bool focused;
        try
        {
            focused = await _services.FocusTerminalAsync(session).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _services.Log.Error($"Focus {session.Agent} {session.SessionId}", ex);
            focused = false;
        }
        if (focused) return;
        UiDispatcher.Post(() => _services.Notify($"{session.Agent.DisplayName()} · {session.DisplayName}", "Terminale non trovato", NoticeKind.Warning));
    }

    private void Toggle()
    {
        if (!ExpandedSessionIds.Remove(SessionId)) ExpandedSessionIds.Add(SessionId);
        Raise(nameof(IsExpanded)); Raise(nameof(ChevronGlyph)); Raise(nameof(SubagentListVisibility));
    }

    private void RaiseSubagentState()
    {
        Raise(nameof(HasSubagents)); Raise(nameof(SubagentSummary));
        Raise(nameof(SummaryVisibility)); Raise(nameof(SubagentListVisibility));
    }

    /// <summary>
    /// Reconciles the rows by agent id (same shape as AgentCardViewModel.SyncSessions): an agent that is still
    /// there keeps its row — and with it its elapsed clock — instead of being rebuilt on every pump refresh.
    /// </summary>
    private void SyncSubagents(DateTimeOffset now)
    {
        var ordered = _session.RunningSubagents
            .OrderByDescending(s => s.StartedAt)
            .ToList();

        var byId = new Dictionary<string, SubagentRowViewModel>(StringComparer.Ordinal);
        foreach (var row in Subagents) byId.TryAdd(row.AgentId, row);

        for (var i = 0; i < ordered.Count; i++)
        {
            if (byId.TryGetValue(ordered[i].AgentId, out var existing))
            {
                existing.Update(ordered[i], now, _pricing);
                var currentIndex = Subagents.IndexOf(existing);
                if (currentIndex != i) Subagents.Move(currentIndex, i);
            }
            else
            {
                Subagents.Insert(i, new SubagentRowViewModel(ordered[i], now, _pricing));
            }
        }
        while (Subagents.Count > ordered.Count) Subagents.RemoveAt(Subagents.Count - 1);
    }
}
