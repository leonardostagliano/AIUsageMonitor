namespace AIUsageMonitor.Core.Notch;

/// <summary>
/// Pure geometry of the notification stack (spec 2026-09-27 §6): its right edge <see cref="GapDip"/> left of whatever
/// the notch occupies at the right edge of the work area, vertically centred on the tab, kept inside the work area.
/// </summary>
public static class NotificationPlacement
{
    /// <summary>Distance between the stack and the tab, the open panel or (notch hidden) the edge of the work area.</summary>
    public const double GapDip = 8;

    /// <summary>
    /// Left/Top in DIP (same convention as <see cref="NotchPlacement.Compute"/>) of a window
    /// <paramref name="windowWidthDip"/> x <paramref name="windowHeightDip"/> whose visible content is inset by
    /// <paramref name="shadowMarginDip"/> on every side: content right edge = area right - RightInsetDip - gap, content
    /// vertically centred on <see cref="NotchAnchor.CenterYPx"/>, window clamped vertically inside the work area (a
    /// window taller than the area starts at its top). The transparent shadow margin may stick out on the right when
    /// the notch is hidden: only the content keeps the gap.
    /// <para>
    /// Like <see cref="NotchPlacement.Compute"/> it also works in physical pixels, the convention of
    /// <c>NotchWindow.Reposition</c>: pass the anchor with <c>DpiScale = 1</c> and <c>RightInsetDip</c> times the target
    /// scale, every size times the target scale and <paramref name="gapDip"/> = <see cref="GapDip"/> times the target
    /// scale; the result is then in pixels.
    /// </para>
    /// </summary>
    public static (double Left, double Top) Compute(NotchAnchor anchor, double windowWidthDip, double windowHeightDip,
        double shadowMarginDip, double gapDip = GapDip)
    {
        var scale = anchor.DpiScale > 0 ? anchor.DpiScale : 1;
        var right = (anchor.AreaLeftPx + anchor.AreaWidthPx) / scale;
        var top = anchor.AreaTopPx / scale;
        var height = anchor.AreaHeightPx / scale;

        var contentRight = right - anchor.RightInsetDip - gapDip;
        var left = contentRight + shadowMarginDip - windowWidthDip;
        var centered = anchor.CenterYPx / scale - windowHeightDip / 2;
        var maxTop = Math.Max(top, top + height - windowHeightDip);
        return (left, Math.Clamp(centered, top, maxTop));
    }
}
