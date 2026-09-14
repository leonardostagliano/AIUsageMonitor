using System.Windows;
using System.Windows.Input;

namespace AIUsageMonitor.App.Notch;

/// <summary>
/// Code-behind del dizionario dei template del notch. Serve solo al click sul nome della sessione: i template vivono
/// in un <c>ResourceDictionary</c> fuso nelle risorse di <see cref="NotchWindow"/>, e un handler dichiarato in XAML
/// deve stare nel code-behind del file che lo dichiara — non in quello della finestra che lo consuma.
/// </summary>
public partial class NotchTemplates : ResourceDictionary
{
    public NotchTemplates() => InitializeComponent();

    /// <summary>
    /// Il click di fissaggio del pannello e' un <c>MouseLeftButtonDown</c> (<c>NotchWindow.Tab_MouseLeftButtonDown</c>):
    /// marcarlo Handled sul nome impedisce che portare in primo piano un terminale fissi o sblocchi anche la notch.
    /// Su una riga senza host l'evento passa invece intatto e il click continua a valere come click sullo sfondo.
    /// </summary>
    private void SessionName_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Row(sender) is { CanFocus: true }) e.Handled = true;
    }

    /// <summary>Il comando parte al rilascio, come per un pulsante: un drag partito altrove non porta in primo piano nulla.</summary>
    private void SessionName_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Row(sender) is not { CanFocus: true } row) return;
        e.Handled = true;
        if (row.FocusTerminalCommand.CanExecute(null)) row.FocusTerminalCommand.Execute(null);
    }

    private static SessionRowViewModel? Row(object sender) => (sender as FrameworkElement)?.DataContext as SessionRowViewModel;
}
