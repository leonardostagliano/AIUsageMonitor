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
}
