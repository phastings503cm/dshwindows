using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.Core;

namespace Vision.App.Views.Chat;

// MARK: - "Files changed (N)" rows (10-19 #20)
//
// Clicking the row's title shows its files; a file opens its diff in the Rewind window (/rewind); "Rewind this turn…" asks,
// then puts the files back (a rewind is a turn of its own, with its own row, so it can be rewound in turn).

public partial class ChatView
{
    private void CheckpointHeader_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CheckpointEntryVM row) return;
        _pinned = false;
        row.IsExpanded = !row.IsExpanded;
    }

    private void CheckpointFile_Click(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session || (sender as FrameworkElement)?.DataContext is not CheckpointFileVM file) return;
        if (RowOf(sender as DependencyObject) is not { } row) return;
        _model.ShowRewind(session, row.TurnId, file.File.Path, ask: false);
    }

    private void ViewCheckpoint_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } session && (sender as FrameworkElement)?.DataContext is CheckpointEntryVM row) _model.ShowRewind(session, row.TurnId, null, ask: false);
    }

    private void RewindTurn_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } session && (sender as FrameworkElement)?.DataContext is CheckpointEntryVM row) RewindFlow.Run(_model, session, row.TurnId);
    }

    // MARK: - A Sensitive chat's strip

    /// <summary>The strip at the top of a Sensitive chat: what the policy means for its model now (green, or red when the model
    /// can't be used), and Turn Off. Hidden for a chat that isn't Sensitive.</summary>
    private void UpdateSensitiveStrip()
    {
        if (_session is not { } session || _model.Host.SensitivityHeader(session) is not { } line)
        {
            SensitiveStrip.Visibility = Visibility.Collapsed;
            return;
        }
        var refused = _model.Host.SensitiveRefusal(session) is not null;
        SensitiveLine.Text = line;
        SensitiveStrip.ToolTip = SensitivityPolicy.Explanation;
        var tint = refused
            ? ThemeService.Brush("SystemFillColorCriticalBrush", Colors.IndianRed)
            : ThemeService.Brush("SystemFillColorSuccessBrush", Colors.SeaGreen);
        SensitiveGlyph.Foreground = tint;
        SensitiveLine.Foreground = refused ? tint : ThemeService.Brush("TextFillColorPrimaryBrush", Colors.Black);
        var color = tint is SolidColorBrush solid ? solid.Color : refused ? Colors.IndianRed : Colors.SeaGreen;
        SensitiveStrip.Background = new SolidColorBrush(Color.FromArgb(0x16, color.R, color.G, color.B));
        SensitiveStrip.Visibility = Visibility.Visible;
    }

    private void SensitiveOff_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } session) _model.Host.SetChatSensitive(session, false);
    }

    /// <summary>The checkpoint row a control inside it belongs to.</summary>
    private static CheckpointEntryVM? RowOf(DependencyObject? node)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { DataContext: CheckpointEntryVM row }) return row;
        return null;
    }
}
