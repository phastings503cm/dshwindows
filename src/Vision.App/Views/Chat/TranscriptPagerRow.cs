using CommunityToolkit.Mvvm.ComponentModel;
using Vision.Core;

namespace Vision.App.Views.Chat;

/// <summary>The row above a windowed transcript ("Load earlier messages · 1,950 earlier") or below one that stops short of
/// the newest row ("Load newer messages · 120 newer", "Jump to latest"). Hidden when there is nothing that way.</summary>
public sealed partial class TranscriptPagerRow(bool top) : ObservableObject
{
    public bool IsTop { get; } = top;
    public bool IsBottom => !IsTop;
    public string Title => IsTop ? "Load earlier messages" : "Load newer messages";
    public string ToolTip => IsTop
        ? "Show the page of messages before these (scrolling to the top does it too)"
        : "Show the page of messages after these (scrolling to the bottom does it too)";

    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _count = "";

    /// <summary>Show <paramref name="remaining"/> rows that way, or hide the row when there are none.</summary>
    public void Update(int remaining)
    {
        IsVisible = remaining > 0;
        Count = remaining <= 0 ? "" : IsTop ? TranscriptPaging.EarlierLabel(remaining) : TranscriptPaging.NewerLabel(remaining);
    }
}
