using AIUsageMonitor.Core.Notifications;

namespace AIUsageMonitor.Tests;

public class QuietModeRulesTests
{
    [Theory]
    [InlineData(2)] // QUNS_BUSY: app a schermo intero (video, browser con F11)
    [InlineData(3)] // QUNS_RUNNING_D3D_FULL_SCREEN: gioco Direct3D a schermo intero
    [InlineData(4)] // QUNS_PRESENTATION_MODE: modalita' presentazione
    [InlineData(7)] // QUNS_APP: app dello Store a schermo intero
    public void Full_screen_and_presentation_states_are_quiet(int state) =>
        Assert.True(QuietModeRules.IsQuietShellState(state));

    [Theory]
    [InlineData(5)]  // QUNS_ACCEPTS_NOTIFICATIONS: lo stato normale (quello letto su questa macchina)
    [InlineData(1)]  // QUNS_NOT_PRESENT: schermo bloccato; le card restano leggibili con UI Automation
    [InlineData(6)]  // QUNS_QUIET_TIME: prima ora dopo un'installazione, non e' "Non disturbare"
    [InlineData(0)]  // valori fuori dall'enumerazione
    [InlineData(8)]
    [InlineData(-1)]
    public void Every_other_shell_state_lets_the_cards_through(int state) =>
        Assert.False(QuietModeRules.IsQuietShellState(state));

    [Fact]
    public void Do_not_disturb_is_on_for_any_non_zero_profile()
    {
        Assert.False(QuietModeRules.IsDoNotDisturb([0, 0, 0, 0]));
        Assert.True(QuietModeRules.IsDoNotDisturb([1, 0, 0, 0])); // solo priorita'
        Assert.True(QuietModeRules.IsDoNotDisturb([2, 0, 0, 0])); // solo sveglie
        // Intero a 32 bit little-endian, non solo il primo byte.
        Assert.True(QuietModeRules.IsDoNotDisturb([0, 1, 0, 0]));
    }

    [Fact]
    public void A_missing_or_truncated_profile_means_off()
    {
        Assert.False(QuietModeRules.IsDoNotDisturb([]));
        Assert.False(QuietModeRules.IsDoNotDisturb([1, 0]));
    }

    [Fact]
    public void Only_the_first_four_bytes_are_the_profile()
    {
        Assert.False(QuietModeRules.IsDoNotDisturb([0, 0, 0, 0, 1, 1, 1, 1]));
        Assert.True(QuietModeRules.IsDoNotDisturb([1, 0, 0, 0, 0, 0, 0, 0]));
    }
}
