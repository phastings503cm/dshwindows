using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Vision.App.Model;

namespace Vision.App.Views.Chat;

// MARK: - "Files changed (N)" rows (10-19 #20)
//
// Clicking the row's title shows its files; a file opens its diff in the Rewind window (/rewind); "Rewind this turn" asks,
// then puts the files back; "Undo rewind" puts back what a rewind replaced.

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
        _model.ShowRewind(session, row.TurnId, file.File.Path);
    }

    private void ViewCheckpoint_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } session && (sender as FrameworkElement)?.DataContext is CheckpointEntryVM row) _model.ShowRewind(session, row.TurnId, null);
    }

    private void RewindTurn_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } session && (sender as FrameworkElement)?.DataContext is CheckpointEntryVM row) RewindFlow.Run(_model, session, row.TurnId);
    }

    private void UndoRewind_Click(object sender, RoutedEventArgs e)
    {
        if (_session is { } session && (sender as FrameworkElement)?.DataContext is CheckpointEntryVM row) RewindFlow.Undo(_model, session, row.TurnId);
    }

    /// <summary>The checkpoint row a control inside it belongs to.</summary>
    private static CheckpointEntryVM? RowOf(DependencyObject? node)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is FrameworkElement { DataContext: CheckpointEntryVM row }) return row;
        return null;
    }
}
