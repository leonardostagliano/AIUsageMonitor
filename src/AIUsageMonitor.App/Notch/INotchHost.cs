namespace AIUsageMonitor.App.Notch;

public interface INotchHost
{
    bool IsNotchVisible { get; }
    void Pin();
    void TogglePin();
    void ToggleVisible();
}
