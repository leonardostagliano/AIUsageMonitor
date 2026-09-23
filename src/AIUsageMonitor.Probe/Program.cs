using AIUsageMonitor.Core.Hooks;
using AIUsageMonitor.Core.Infrastructure;
using AIUsageMonitor.Core.Models;
using AIUsageMonitor.Core.Pricing;
using AIUsageMonitor.Core.Usage;
using System.Text.Json;

if (args.Contains("--install-hooks"))
{
    var inst = new HookInstaller(AppPaths.Default, new SystemClock());
    foreach (var agent in new[] { AgentKind.Claude, AgentKind.Codex })
        Console.WriteLine($"{agent.DisplayName()}: {inst.Install(agent).Status}");
    return;
}
if (args.Contains("--remove-hooks"))
{
    var inst = new HookInstaller(AppPaths.Default, new SystemClock());
    foreach (var agent in new[] { AgentKind.Claude, AgentKind.Codex })
        Console.WriteLine($"{agent.DisplayName()}: {inst.Remove(agent).Status}");
    return;
}
if (args.Contains("--update-price-snapshot"))
{
    // Rigenera la copia del listino imbarcata nell'exe (stesso filtro della cache): dotnet run --project src/AIUsageMonitor.Probe -- --update-price-snapshot
    var target = args.SkipWhile(a => a != "--update-price-snapshot").Skip(1).FirstOrDefault()
                 ?? Path.Combine("src", "AIUsageMonitor.Core", "Pricing", "prices-snapshot.json");
    using var http = new HttpClient();
    http.DefaultRequestHeaders.UserAgent.ParseAdd("AIUsageMonitor");
    using var list = JsonDocument.Parse(await http.GetStringAsync(PriceListServiceOptions.DefaultSourceUrl));
    var models = LiteLlmPriceParser.Trim(list.RootElement, out var anthropic, out var openai);
    PriceListService.WriteListFile(target, DateTimeOffset.UtcNow, null, PriceListServiceOptions.DefaultSourceUrl.ToString(), models);
    Console.WriteLine($"{target}: {anthropic} modelli Anthropic, {openai} OpenAI");
    return;
}

var paths = AppPaths.Default;
var clock = new SystemClock();
var now = clock.UtcNow;

Console.WriteLine("== Quota ==");
var providers = new IUsageProvider[]
{
    new ClaudeUsageProvider(paths, new HttpClient(), clock),
    new CodexUsageProvider(paths, clock)
};
foreach (var provider in providers)
{
    var snap = await provider.FetchAsync();
    Console.WriteLine($"{snap.Agent.DisplayName()}  piano={snap.PlanLabel ?? "-"}  stato={snap.Status} {snap.StatusMessage}");
    foreach (var w in snap.Windows)
    {
        var reset = w.ResetsAt is { } r ? $"reset tra {CountdownFormatter.Until(r, now)}" : "reset sconosciuto";
        Console.WriteLine($"   {w.Label,-12} {w.Percent,5:0}%  {w.Severity,-8} {reset}");
    }
    if (snap.ExtraUsage is not null) Console.WriteLine($"   {snap.ExtraUsage}");
}

Console.WriteLine();
Console.WriteLine("== Hook ==");
var installer = new HookInstaller(paths, clock);
foreach (var agent in new[] { AgentKind.Claude, AgentKind.Codex })
{
    var status = installer.GetStatus(agent);
    Console.WriteLine($"{agent.DisplayName(),-12} {status.Status,-14} {status.Detail}");
}

Console.WriteLine();
Console.WriteLine("== Sessioni (ultime 24h dal file eventi) ==");
// One resolver for the whole run: it caches hits and misses, so a session without cwd is not re-scanned on every event.
var codexResolver = new CodexSessionResolver(paths.CodexSessionsDir, clock);
var tracker = new SessionTracker(clock, (agent, id) => agent == AgentKind.Codex ? codexResolver.ResolveCwd(id) : null);
var reader = new HookEventReader(paths.EventsFile, paths.RotatedEventsFile);
tracker.ApplySilently(reader.ReadAll(now - TimeSpan.FromHours(24)));
tracker.RemoveStaleSilently(TimeSpan.FromHours(12));
if (tracker.Sessions.Count == 0) Console.WriteLine("nessuna sessione (installa gli hook e avvia un agente)");
foreach (var s in tracker.Sessions)
    Console.WriteLine($"{s.Agent.DisplayName(),-12} {s.DisplayName,-30} {s.PhaseLabel,-14} {CountdownFormatter.Since(s.LastEventAt, now)} fa  {s.Message}");
