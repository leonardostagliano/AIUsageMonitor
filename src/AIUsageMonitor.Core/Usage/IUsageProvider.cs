using AIUsageMonitor.Core.Models;

namespace AIUsageMonitor.Core.Usage;

public interface IUsageProvider
{
    AgentKind Agent { get; }

    /// <summary>Never throws: failures are reported through <see cref="UsageSnapshot.Status"/>.</summary>
    Task<UsageSnapshot> FetchAsync(CancellationToken cancellationToken = default);
}
