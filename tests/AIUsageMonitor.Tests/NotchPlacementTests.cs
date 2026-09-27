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

    // The centre NotchWindow publishes in its Anchor (notch hidden, or before its HWND exists) is the centre of the
    // window NotchPlacement places, converted to physical pixels with the scale of the notch monitor.
    [Theory]
    [InlineData(0, 0, 3840, 2088, 1.5, 300, 120)]         // 150 %, offset down
    [InlineData(2560, -200, 2560, 1400, 1.25, 180, -60)]  // secondary monitor with a negative top, offset up
    [InlineData(-1920, 0, 1920, 1040, 1.0, 240, 0)]       // left monitor at 100 %
    [InlineData(0, 40, 3840, 2120, 2.0, 300, 5000)]       // 200 %, pushed past the bottom: clamped
    [InlineData(0, 40, 3840, 2120, 1.75, 300, -5000)]     // 175 %, pushed past the top: clamped
    [InlineData(0, 0, 1920, 200, 1.5, 300, 0)]            // notch taller than the work area: starts at its top
    public void The_tab_centre_in_pixels_is_the_centre_of_the_notch_NotchPlacement_places(
        double x, double y, double w, double h, double scale, double heightDip, double offsetDip)
    {
        var (_, topDip) = NotchPlacement.Compute(x, y, w, h, scale, 320, heightDip, offsetDip);

        Assert.Equal((topDip + heightDip / 2) * scale, NotchPlacement.CenterYPx(x, y, w, h, scale, heightDip, offsetDip), 6);
    }

    [Fact]
    public void A_non_positive_scale_centres_the_tab_at_100_percent() =>
        Assert.Equal(NotchPlacement.CenterYPx(0, 0, 1920, 1040, 1.0, 300, 120),
            NotchPlacement.CenterYPx(0, 0, 1920, 1040, 0, 300, 120), 6);
}
