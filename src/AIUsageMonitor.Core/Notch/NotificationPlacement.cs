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
    /// window taller than the area starts at its top). Only the content keeps the gap: on the right the shadow margin
    /// reaches past it into the tab, the open panel or (notch hidden) past the work area, see <see cref="RightOverlap"/>.
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

    /// <summary>
    /// How far a window at <paramref name="windowLeft"/>, <paramref name="windowWidth"/> wide, reaches into what the
    /// notch occupies at the right edge (from area right - <see cref="NotchAnchor.RightInsetDip"/> to area right: the
    /// tab, the open or pinned panel, or with the notch hidden whatever lies past the work area); 0 when it ends before.
    /// A window placed by <see cref="Compute"/> always reaches in by the shadow margin minus the gap (16 - 8 = 8 DIP),
    /// in every state. The host clips that strip: the shadow is faint but not transparent out to the window edge, and a
    /// layered window takes the mouse wherever its alpha is not zero, so it would draw over the left edge of the tab or
    /// panel and swallow their hover and clicks (the host is above the notch in z-order), or those of the next monitor
    /// or of a taskbar at the right. Same units as <see cref="Compute"/>: DIP, or pixels with the anchor at
    /// <c>DpiScale = 1</c> and <c>RightInsetDip</c> times the target scale.
    /// </summary>
    public static double RightOverlap(NotchAnchor anchor, double windowLeft, double windowWidth)
    {
        var scale = anchor.DpiScale > 0 ? anchor.DpiScale : 1;
        var notchLeft = (anchor.AreaLeftPx + anchor.AreaWidthPx) / scale - anchor.RightInsetDip;
        return Math.Max(0, windowLeft + windowWidth - notchLeft);
    }
}
