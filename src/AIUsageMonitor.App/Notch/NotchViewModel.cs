using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using AIUsageMonitor.App.Common;
using AIUsageMonitor.App.Startup;
using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.App.Notch;

public sealed class NotchViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _tick;

    public ObservableCollection<AgentCardViewModel> Agents { get; } = new();

    public NotchViewModel(AppServices services)
    {
        _services = services;
        Rebuild();
        services.StateChanged += () => UiDispatcher.Post(Refresh);
        services.Settings.Changed += _ => UiDispatcher.Post(Rebuild);
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => { foreach (var agent in Agents) agent.TickClocks(); };
        _tick.Start();
    }

    private void Rebuild()
    {
        var enabled = _services.EnabledAgents().ToList();
        if (Agents.Select(a => a.Agent).SequenceEqual(enabled)) { Refresh(); return; }
        Agents.Clear();
        foreach (var agent in enabled)
            Agents.Add(new AgentCardViewModel(agent, _services, IconFor(agent)));
    }

    private void Refresh()
    {
        foreach (var agent in Agents) agent.Refresh();
    }

    private static Geometry IconFor(AgentKind agent) =>
        (Geometry)Application.Current.FindResource(agent == AgentKind.Claude ? "ClaudeIcon" : "CodexIcon");
}
