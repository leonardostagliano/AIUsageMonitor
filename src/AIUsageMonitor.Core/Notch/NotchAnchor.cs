namespace AIUsageMonitor.Core.Notch;

/// <summary>
/// Where the notch is, as the notification stack needs it (spec 2026-09-27 §6): the work area of the notch monitor in
/// physical pixels, that monitor's DPI scale, the absolute Y in pixels of the tab centre and how many DIP the notch
/// occupies at the right edge of the work area (the tab width, the panel width while the panel is open, 0 while the
/// notch is hidden).
/// </summary>
public sealed record NotchAnchor(double AreaLeftPx, double AreaTopPx, double AreaWidthPx, double AreaHeightPx,
    double DpiScale, double CenterYPx, double RightInsetDip)
{
    /// <summary>
    /// What the notch occupies at the right edge: nothing while it is hidden (even if it was pinned open before being
    /// hidden), the panel while it is open or pinned, the tab otherwise.
    /// </summary>
    public static double RightInset(bool notchVisible, bool panelOpen, double panelWidthDip, double tabWidthDip) =>
        !notchVisible ? 0 : panelOpen ? panelWidthDip : tabWidthDip;
}
