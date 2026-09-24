using AIUsageMonitor.Core.Sessions;

namespace AIUsageMonitor.Tests.Helpers;

/// <summary>A process table the test edits: a pid not listed is dead.</summary>
public sealed class FakeProcessProbe : IProcessProbe
{
    public Dictionary<int, ProcessSnapshot> Processes { get; } = new();

    public int Queries { get; private set; }

    public FakeProcessProbe Start(int pid, long startedAtFileTime)
    {
        Processes[pid] = ProcessSnapshot.Alive(startedAtFileTime);
        return this;
    }

    public void Kill(int pid) => Processes.Remove(pid);

    public ProcessSnapshot Query(int pid)
    {
        Queries++;
        return Processes.TryGetValue(pid, out var snapshot) ? snapshot : ProcessSnapshot.Dead;
    }
}
