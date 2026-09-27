using AIUsageMonitor.Core.Notch;

namespace AIUsageMonitor.Tests;

public class NotificationPlacementTests
{
    // The host window: 340 DIP of cards plus a 16 DIP shadow margin on every side.
    private const double Width = 372;
    private const double Height = 200;
    private const double Margin = 16;

    private static NotchAnchor FullHd(double centerYPx = 520, double insetDip = 42) =>
        new(0, 0, 1920, 1040, 1.0, centerYPx, insetDip);

    [Theory]
    [InlineData(42, 1514)]   // tab
    [InlineData(34, 1522)]   // compact tab
    [InlineData(320, 1236)]  // panel open or pinned
    [InlineData(0, 1556)]    // notch hidden
    public void The_cards_end_8_DIP_left_of_what_the_notch_occupies(double insetDip, double expectedLeft)
    {
        var (left, top) = NotificationPlacement.Compute(FullHd(insetDip: insetDip), Width, Height, Margin);

        Assert.Equal(expectedLeft, left, 6);
        Assert.Equal(1920 - insetDip - NotificationPlacement.GapDip, left + Width - Margin, 6); // right edge of the cards
        Assert.Equal(420, top, 6);
    }

    [Fact]
    public void With_the_notch_hidden_only_the_transparent_margin_passes_the_edge()
    {
        var (left, _) = NotificationPlacement.Compute(FullHd(insetDip: 0), Width, Height, Margin);

        Assert.Equal(1912, left + Width - Margin, 6);
        Assert.Equal(1928, left + Width, 6);
    }

    [Theory]
    [InlineData(700, 600)]
    [InlineData(300, 200)]
    public void The_stack_follows_the_tab_centre(double centerYPx, double expectedTop)
    {
        var (_, top) = NotificationPlacement.Compute(FullHd(centerYPx), Width, Height, Margin);
        Assert.Equal(expectedTop, top, 6);
    }

    [Fact]
    public void The_stack_is_centred_on_the_notch_placed_by_NotchPlacement()
    {
        // Notch 320x300 with a vertical offset of 120: its centre is where the stack centres.
        var (_, notchTop) = NotchPlacement.Compute(0, 0, 1920, 1040, 1.0, 320, 300, 120);
        var notchCentre = notchTop + 150;

        var (_, top) = NotificationPlacement.Compute(FullHd(notchCentre), Width, 136, Margin);

        Assert.Equal(notchCentre, top + 136 / 2.0, 6);
    }

    [Fact]
    public void The_stack_centres_on_the_tab_NotchWindow_publishes_at_150_percent_with_an_offset()
    {
        // Notch 320x300 DIP moved down by 120 DIP on a 3840x2088 px work area at 150 %: the anchor carries the centre
        // NotchWindow computes, and the stack must end up centred on the notch NotchPlacement places, in DIP.
        var (_, notchTopDip) = NotchPlacement.Compute(0, 0, 3840, 2088, 1.5, 320, 300, 120);
        var anchor = new NotchAnchor(0, 0, 3840, 2088, 1.5, NotchPlacement.CenterYPx(0, 0, 3840, 2088, 1.5, 300, 120), 42);

        var (_, top) = NotificationPlacement.Compute(anchor, Width, 136, Margin);

        Assert.Equal(notchTopDip + 300 / 2.0, top + 136 / 2.0, 6);
    }

    [Fact]
    public void The_window_is_clamped_inside_the_work_area()
    {
        Assert.Equal(0, NotificationPlacement.Compute(FullHd(50), Width, Height, Margin).Top, 6);
        Assert.Equal(840, NotificationPlacement.Compute(FullHd(1000), Width, Height, Margin).Top, 6);

        // Taskbar at the top: the work area starts at y = 40.
        var belowTaskbar = new NotchAnchor(0, 40, 1920, 1000, 1.0, 60, 42);
        Assert.Equal(40, NotificationPlacement.Compute(belowTaskbar, Width, Height, Margin).Top, 6);
        Assert.Equal(840, NotificationPlacement.Compute(belowTaskbar with { CenterYPx = 1030 }, Width, Height, Margin).Top, 6);
    }

    [Fact]
    public void A_stack_taller_than_the_work_area_starts_at_its_top()
    {
        var shortArea = new NotchAnchor(0, 40, 1920, 150, 1.0, 115, 42);
        Assert.Equal(40, NotificationPlacement.Compute(shortArea, Width, Height, Margin).Top, 6);
    }

    [Fact]
    public void Converts_pixels_to_DIP_at_150_percent()
    {
        // 3840x2160 monitor with a 72 px taskbar at 150 %: 2560 DIP wide.
        var anchor = new NotchAnchor(0, 0, 3840, 2088, 1.5, 1044, 42);

        var (left, top) = NotificationPlacement.Compute(anchor, Width, Height, Margin);

        Assert.Equal(2560 - 42 - 8 + 16 - 372, left, 6);
        Assert.Equal(1044 / 1.5 - 100, top, 6);
    }

    [Fact]
    public void Works_on_a_secondary_monitor_with_a_negative_origin()
    {
        // Monitor left of and above the primary one, at 125 %: x from -1920 to 0 px, y from -300 px.
        var anchor = new NotchAnchor(-1920, -300, 1920, 1040, 1.25, -300 + 520, 42);

        var (left, top) = NotificationPlacement.Compute(anchor, Width, Height, Margin);
        Assert.Equal(0 - 42 - 8 + 16 - 372, left, 6);
        Assert.Equal(220 / 1.25 - 100, top, 6);

        var (_, clamped) = NotificationPlacement.Compute(anchor with { CenterYPx = -280 }, Width, Height, Margin);
        Assert.Equal(-300 / 1.25, clamped, 6);
    }

    [Theory]
    [InlineData(0, 0, 3840, 2088, 1.5, 1044, 42)]
    [InlineData(0, 0, 3840, 2088, 1.5, 60, 320)]        // clamped at the top
    [InlineData(-1920, -300, 1920, 1040, 1.25, 220, 0)] // negative origin, notch hidden
    public void The_physical_pixel_convention_gives_the_DIP_result_times_the_scale(
        double x, double y, double w, double h, double scale, double centerYPx, double insetDip)
    {
        var anchor = new NotchAnchor(x, y, w, h, scale, centerYPx, insetDip);
        var dip = NotificationPlacement.Compute(anchor, Width, Height, Margin);

        // What NotificationHostWindow.Reposition passes: scale 1 on the area (already in px), every DIP length times the scale.
        var px = NotificationPlacement.Compute(
            anchor with { DpiScale = 1.0, RightInsetDip = insetDip * scale },
            Width * scale, Height * scale, Margin * scale, NotificationPlacement.GapDip * scale);

        Assert.Equal(dip.Left * scale, px.Left, 6);
        Assert.Equal(dip.Top * scale, px.Top, 6);
    }

    // The shadow is faint but not transparent out to the window edge, and a layered window takes the mouse wherever
    // its alpha is not zero: the host clips what sticks out of the work area, so nothing lands on the next monitor.
    [Theory]
    [InlineData(42, 0)]     // tab
    [InlineData(34, 0)]     // compact tab
    [InlineData(320, 0)]    // panel open
    [InlineData(0, 8)]      // notch hidden: the outer half of the shadow margin
    public void Only_a_hidden_notch_lets_the_window_stick_out_of_the_work_area(double insetDip, double expected)
    {
        var anchor = FullHd(insetDip: insetDip);
        var (left, _) = NotificationPlacement.Compute(anchor, Width, Height, Margin);

        Assert.Equal(expected, NotificationPlacement.RightOverhang(anchor, left, Width), 6);
    }

    [Fact]
    public void The_overhang_follows_the_physical_pixel_convention()
    {
        // Monitor left of the primary one at 125 %, notch hidden.
        var anchor = new NotchAnchor(-1920, -300, 1920, 1040, 1.25, 220, 0);
        var (leftDip, _) = NotificationPlacement.Compute(anchor, Width, Height, Margin);
        Assert.Equal(8, NotificationPlacement.RightOverhang(anchor, leftDip, Width), 6);

        var px = anchor with { DpiScale = 1.0 };
        var (leftPx, _) = NotificationPlacement.Compute(px, Width * 1.25, Height * 1.25, Margin * 1.25, NotificationPlacement.GapDip * 1.25);
        Assert.Equal(8 * 1.25, NotificationPlacement.RightOverhang(px, leftPx, Width * 1.25), 6);
    }

    [Fact]
    public void A_non_positive_scale_counts_as_100_percent()
    {
        var expected = NotificationPlacement.Compute(FullHd(), Width, Height, Margin);
        Assert.Equal(expected, NotificationPlacement.Compute(FullHd() with { DpiScale = 0 }, Width, Height, Margin));
        Assert.Equal(expected, NotificationPlacement.Compute(FullHd() with { DpiScale = -2 }, Width, Height, Margin));
    }

    [Theory]
    [InlineData(true, false, 42)]
    [InlineData(true, true, 320)]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 0)]   // hidden while pinned open: nothing at the edge
    public void The_right_inset_is_the_tab_the_open_panel_or_nothing(bool visible, bool panelOpen, double expected) =>
        Assert.Equal(expected, NotchAnchor.RightInset(visible, panelOpen, 320, 42));
}
