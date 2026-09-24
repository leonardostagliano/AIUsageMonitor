using System.Windows;
using System.Windows.Controls;

namespace AIUsageMonitor.App.Controls;

/// <summary>
/// One-row panel for the segmented tabs. Each child is as wide as its content, plus an equal share of whatever space
/// is left. A long header like "Aggiornamenti" keeps all its text where equal columns would cut it off. If the row is
/// narrower than the children together, each one shrinks in proportion to its width.
/// </summary>
public sealed class SegmentPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var total = 0d;
        var height = 0d;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            total += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        if (!double.IsFinite(availableSize.Width) || total <= availableSize.Width) return new Size(total, height);

        // Too narrow: measure again at the width each child will get, so it lays out inside it.
        var scale = availableSize.Width / total;
        foreach (UIElement child in InternalChildren)
            child.Measure(new Size(child.DesiredSize.Width * scale, availableSize.Height));
        return new Size(availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var total = 0d;
        var shown = 0;
        foreach (UIElement child in InternalChildren)
        {
            total += child.DesiredSize.Width;
            if (child.Visibility != Visibility.Collapsed) shown++;
        }
        var extra = shown > 0 ? Math.Max(0, finalSize.Width - total) / shown : 0;
        var scale = total > finalSize.Width && total > 0 ? finalSize.Width / total : 1;
        var x = 0d;
        foreach (UIElement child in InternalChildren)
        {
            var width = child.Visibility == Visibility.Collapsed ? 0 : child.DesiredSize.Width * scale + extra;
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
            x += width;
        }
        return finalSize;
    }
}
