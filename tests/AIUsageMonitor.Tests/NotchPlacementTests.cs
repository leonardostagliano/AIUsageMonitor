using AIUsageMonitor.Core.Notch;

namespace AIUsageMonitor.Tests;

public class NotchPlacementTests
{
    [Fact]
    public void Anchors_to_the_right_edge_and_centers_vertically()
    {
        // 1920x1040 work area at 100% DPI, window 320x200
        var (left, top) = NotchPlacement.Compute(0, 0, 1920, 1040, 1.0, 320, 200, 0);
        Assert.Equal(1600, left);
        Assert.Equal(420, top);
    }

    [Fact]
    public void Converts_pixels_to_dips_and_applies_offset_on_a_secondary_monitor()
    {
        // second monitor at x=2560 px, 150% DPI: 2560..(2560+3840) px wide, 2160 px tall
        var (left, top) = NotchPlacement.Compute(2560, 0, 3840, 2160, 1.5, 320, 200, 100);
        Assert.Equal((2560 + 3840) / 1.5 - 320, left, 3);
        Assert.Equal((2160 / 1.5 - 200) / 2 + 100, top, 3);
    }

    [Fact]
    public void Clamps_inside_the_work_area()
    {
        var (_, top) = NotchPlacement.Compute(0, 0, 1920, 1040, 1.0, 320, 200, 5000);
        Assert.Equal(840, top);
        var (_, top2) = NotchPlacement.Compute(0, 0, 1920, 1040, 1.0, 320, 200, -5000);
        Assert.Equal(0, top2);
        var (_, top3) = NotchPlacement.Compute(0, 0, 1920, 100, 1.0, 320, 200, 0);
        Assert.Equal(0, top3);
    }
}
