namespace AIUsageMonitor.Core.Notch;

/// <summary>Pure geometry: where to put a right-anchored, vertically centered window on a monitor work area given in pixels.</summary>
public static class NotchPlacement
{
    public static (double Left, double Top) Compute(
        double areaLeftPx, double areaTopPx, double areaWidthPx, double areaHeightPx,
        double dpiScale, double widthDip, double heightDip, double verticalOffsetDip)
    {
        if (dpiScale <= 0) dpiScale = 1;
        var right = (areaLeftPx + areaWidthPx) / dpiScale;
        var top = areaTopPx / dpiScale;
        var height = areaHeightPx / dpiScale;

        var left = right - widthDip;
        var centered = top + (height - heightDip) / 2 + verticalOffsetDip;
        var maxTop = Math.Max(top, top + height - heightDip);
        return (left, Math.Clamp(centered, top, maxTop));
    }

    /// <summary>
    /// Absolute Y, in physical pixels, of the vertical centre of a notch window <paramref name="heightDip"/> tall placed
    /// by <see cref="Compute"/> on the work area of a monitor at <paramref name="dpiScale"/>. It is computed the way
    /// <c>NotchWindow.Reposition</c> places the window (scale 1 on the area, already in pixels, and every DIP length
    /// times the scale), so it is the tab centre the notch publishes in its anchor while the window is hidden or has no
    /// HWND yet: the tab is vertically centred in the window.
    /// </summary>
    public static double CenterYPx(
        double areaLeftPx, double areaTopPx, double areaWidthPx, double areaHeightPx,
        double dpiScale, double heightDip, double verticalOffsetDip)
    {
        if (dpiScale <= 0) dpiScale = 1;
        var heightPx = heightDip * dpiScale;
        var (_, topPx) = Compute(areaLeftPx, areaTopPx, areaWidthPx, areaHeightPx, 1.0, 0, heightPx, verticalOffsetDip * dpiScale);
        return topPx + heightPx / 2;
    }
}
