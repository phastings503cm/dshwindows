using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Vision.App.Infrastructure;
using Vision.App.Model;
using Vision.Core;

namespace Vision.App.Views.Chat;

/// <summary>The transcript's turn-phase row: under the newest entry while a turn runs, and the finished turn's results card
/// until the next turn replaces it.</summary>
public sealed partial class PhaseFooter : ObservableObject
{
    private SessionVM? _session;

    [ObservableProperty] private bool _isVisible;

    /// <summary>Raised when the card should redraw (the phase moved, or another chat is shown).</summary>
    public event Action? Changed;

    public SessionVM? Session
    {
        get => _session;
        set
        {
            if (_session is not null)
            {
                _session.PhaseChanged -= OnPhase;
                _session.PropertyChanged -= OnSession;
            }
            _session = value;
            if (_session is not null)
            {
                _session.PhaseChanged += OnPhase;
                _session.PropertyChanged += OnSession;
            }
            OnPhase();
        }
    }

    private void OnSession(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SessionVM.Running) or nameof(SessionVM.TurnPhase)) OnPhase();
    }

    private void OnPhase()
    {
        IsVisible = _session is { PhasesEnabled: true, TurnPhase: not null };
        Changed?.Invoke();
    }
}

/// <summary>The live phase card: Planning (the draft streaming in) → Execution (the green working badge, runtime against the
/// estimate, stamped steps) → Validation → Results ("Completed with results" and the evidence). The same card and words as
/// the Mac's PhaseCard.</summary>
public sealed class PhaseCardView : UserControl
{
    private PhaseFooter? _footer;
    private readonly Border _card = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(10, 9, 10, 10), Margin = new Thickness(28, 0, 0, 14) };
    private readonly StackPanel _root = new();
    private readonly TextBlock _runtime = Ui.Text("", 11, brushKey: "TextFillColorSecondaryBrush", wrap: false);
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _showSteps;
    private bool _showDraft;
    private string? _turnId;
    private TurnPhase? _drawnPhase;

    public PhaseCardView()
    {
        _card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        _card.Child = _root;
        _runtime.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        Content = _card;
        _tick.Tick += (_, _) => Tick();
        DataContextChanged += (_, _) => Attach(DataContext as PhaseFooter);
        Unloaded += (_, _) => _tick.Stop();
        Loaded += (_, _) => Redraw();
    }

    private void Attach(PhaseFooter? footer)
    {
        if (_footer is not null) _footer.Changed -= OnChanged;
        _footer = footer;
        if (_footer is not null) _footer.Changed += OnChanged;
        Redraw();
    }

    private bool _queued;

    private void OnChanged()
    {
        // Many changes in one moment (a tool finishing, the plan settling) redraw once.
        if (_queued) return;
        _queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _queued = false;
            Redraw();
        });
    }

    private TurnPhaseState? State => _footer?.Session?.TurnPhase;

    private void Tick()
    {
        if (State is not { } state) return;
        _runtime.Text = state.IsOver ? "" : PhaseText.RuntimeLine(state, DateTimeOffset.Now);
        // The draft grows between redraws: keep the planning body current.
        if (state.Phase == TurnPhase.Planning) Redraw();
    }

    public void Redraw()
    {
        _root.Children.Clear();
        if (State is not { } state)
        {
            _tick.Stop();
            return;
        }
        if (state.TurnId != _turnId)
        {
            _turnId = state.TurnId;
            _showSteps = false;
            _showDraft = false;
        }
        _drawnPhase = state.Phase;
        var outline = state.EndedReason is not null ? "CardStrokeColorDefaultBrush"
            : state.Phase == TurnPhase.Planning ? "AccentFillColorDefaultBrush"
            : state.Phase == TurnPhase.Results && state.LastValidationFailure is not null ? "SystemFillColorCautionBrush"
            : "SystemFillColorSuccessBrush";
        _card.SetResourceReference(Border.BorderBrushProperty, outline);

        _root.Children.Add(Strip(state));
        if (state.EndedReason is not null) _root.Children.Add(EndedBody(state));
        else
        {
            switch (state.Phase)
            {
                case TurnPhase.Planning: _root.Children.Add(PlanningBody(state)); break;
                case TurnPhase.Execution: _root.Children.Add(ExecutionBody(state)); break;
                case TurnPhase.Validation: _root.Children.Add(Line(Icons.Shield, "Validating the work against the plan…", "SystemFillColorSuccessBrush", bold: true)); break;
                default: _root.Children.Add(ResultsBody(state)); break;
            }
        }
        _runtime.Text = state.IsOver ? "" : PhaseText.RuntimeLine(state, DateTimeOffset.Now);
        if (state.IsOver) _tick.Stop();
        else _tick.Start();
    }

    // MARK: The strip

    private UIElement Strip(TurnPhaseState state)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 0, 0, 7), LastChildFill = false };
        DockPanel.SetDock(_runtime, Dock.Right);
        if (_runtime.Parent is Panel old) old.Children.Remove(_runtime);
        dock.Children.Add(_runtime);
        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        for (var i = 0; i < TurnPhases.All.Count; i++)
        {
            if (i > 0)
            {
                var chevron = Ui.Glyph(Icons.ChevronRight, 8, "TextFillColorTertiaryBrush");
                chevron.Margin = new Thickness(7, 0, 7, 0);
                chips.Children.Add(chevron);
            }
            chips.Children.Add(Chip(TurnPhases.All[i], state));
        }
        DockPanel.SetDock(chips, Dock.Left);
        dock.Children.Add(chips);
        return dock;
    }

    private UIElement Chip(TurnPhase phase, TurnPhaseState state)
    {
        var live = phase == state.Phase && state.EndedReason is null;
        var done = state.Phase == TurnPhase.Results ? phase != TurnPhase.Results || state.EndedReason is null
            : (int)phase < (int)state.Phase;
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        FrameworkElement mark;
        if (live && phase != TurnPhase.Results && phase != TurnPhase.Planning)
        {
            var dot = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7 };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "SystemFillColorSuccessBrush");
            Pulse(dot, 0.7);
            mark = dot;
        }
        else if (live && phase == TurnPhase.Planning)
        {
            mark = new Spinner { Width = 9, Height = 9, Foreground = ThemeService.Brush("AccentFillColorDefaultBrush", Colors.SteelBlue) };
        }
        else if (done)
        {
            mark = Ui.Glyph(Icons.CheckMark, 8, "SystemFillColorSuccessBrush");
        }
        else
        {
            var ring = new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, StrokeThickness = 1 };
            ring.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "TextFillColorTertiaryBrush");
            mark = ring;
        }
        mark.VerticalAlignment = VerticalAlignment.Center;
        mark.Margin = new Thickness(0, 0, 4, 0);
        panel.Children.Add(mark);
        panel.Children.Add(Ui.Text(phase.Label(), 11, live ? FontWeights.SemiBold : FontWeights.Normal,
            live ? "TextFillColorPrimaryBrush" : done ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush", wrap: false));
        return panel;
    }

    /// <summary>The semi-animated part: a slow fade in and out, forever, while the phase is live.</summary>
    private static void Pulse(UIElement element, double seconds)
    {
        var animation = new DoubleAnimation(1.0, 0.45, TimeSpan.FromSeconds(seconds)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
        element.BeginAnimation(OpacityProperty, animation);
    }

    // MARK: Bodies

    private UIElement PlanningBody(TurnPhaseState state)
    {
        var panel = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(Ui.Text("Drafting the plan", 11.5, FontWeights.SemiBold, wrap: false));
        if (state.TaskKind is { } kind)
        {
            var label = Ui.Text("  ·  " + kind.Label(), 11, brushKey: "TextFillColorSecondaryBrush", wrap: false);
            head.Children.Add(label);
        }
        panel.Children.Add(head);
        if (state.Draft.Trim().Length == 0)
        {
            var reading = Ui.Text("Reading the ask…", 11, brushKey: "TextFillColorTertiaryBrush");
            reading.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(reading);
            return panel;
        }
        var draft = Ui.Text(_showDraft ? state.Draft.Trim() : TextUtil.Suffix(state.Draft.Trim(), 400), 11, brushKey: "TextFillColorSecondaryBrush");
        draft.Margin = new Thickness(0, 4, 0, 2);
        if (!_showDraft)
        {
            draft.MaxHeight = 48;
            draft.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        panel.Children.Add(draft);
        panel.Children.Add(Toggle(_showDraft ? "Show less" : "Show the draft", () => _showDraft = !_showDraft));
        return panel;
    }

    private UIElement ExecutionBody(TurnPhaseState state)
    {
        var panel = new StackPanel();
        var head = new DockPanel();
        var toggle = Toggle(_showSteps ? "Hide what it's doing" : "What it's doing", () => _showSteps = !_showSteps);
        DockPanel.SetDock(toggle, Dock.Right);
        head.Children.Add(toggle);
        // The green, semi-animated working badge.
        var badge = Ui.Glyph(Icons.Wrench, 12, "SystemFillColorSuccessBrush");
        badge.Margin = new Thickness(0, 0, 7, 0);
        Pulse(badge, 0.9);
        DockPanel.SetDock(badge, Dock.Left);
        head.Children.Add(badge);
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(Ui.Text("Executing", 11.5, FontWeights.SemiBold, "SystemFillColorSuccessBrush", wrap: false));
        if (state.EtaSeconds is { } eta)
        {
            var est = Ui.Text(" · est. " + TurnEstimator.Phrase(eta), 11.5, brushKey: "TextFillColorSecondaryBrush", wrap: false);
            est.ToolTip = state.EtaBasis;
            line.Children.Add(est);
        }
        head.Children.Add(line);
        panel.Children.Add(head);
        if (state is { Loopbacks: > 0, LastValidationFailure: { } failure })
        {
            var back = Ui.Text($"Validation sent it back ({state.Loopbacks}{(state.LoopbackCap is { } cap ? $" of {cap}" : "")} retries): {failure}", 11, brushKey: "SystemFillColorCautionBrush");
            back.Margin = new Thickness(0, 4, 0, 0);
            panel.Children.Add(back);
        }
        if (_showSteps) panel.Children.Add(Steps(state));
        return panel;
    }

    private UIElement ResultsBody(TurnPhaseState state)
    {
        var panel = new StackPanel();
        var issues = state.LastValidationFailure is not null;
        var head = new DockPanel();
        if (state.FinishedAt is { } finished)
        {
            var time = Ui.Text(PhaseText.Stamp(finished), 10.5, brushKey: "TextFillColorTertiaryBrush", wrap: false);
            time.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            time.ToolTip = Formatting.FullStamp(finished);
            DockPanel.SetDock(time, Dock.Right);
            head.Children.Add(time);
        }
        var brush = issues ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush";
        var glyph = Ui.Glyph(issues ? Icons.Warning : Icons.Completed, 14, brush);
        glyph.Margin = new Thickness(0, 0, 7, 0);
        DockPanel.SetDock(glyph, Dock.Left);
        head.Children.Add(glyph);
        head.Children.Add(Ui.Text(state.Headline, 12.5, FontWeights.SemiBold, brush, wrap: false));
        panel.Children.Add(head);
        var summary = Ui.Text(state.Steps.LastOrDefault()?.Text ?? "", 11, brushKey: "TextFillColorSecondaryBrush");
        summary.Margin = new Thickness(21, 2, 0, 0);
        var meta = state.StartedAt is { } start && state.FinishedAt is { } end
            ? $"  ·  took {(end - start).FormattedDuration()}" + (PhaseText.VersusEstimate((end - start).TotalSeconds, state.EtaSeconds) is { } versus ? $" ({versus})" : "")
              + (state.Loopbacks > 0 ? $"  ·  {state.Loopbacks + 1} attempts" : "")
            : "";
        summary.Text += meta;
        panel.Children.Add(summary);
        if (state.Evidence.Count > 0)
        {
            panel.Children.Add(Rule());
            foreach (var item in state.Evidence) panel.Children.Add(EvidenceRow(item));
        }
        panel.Children.Add(Toggle(_showSteps ? "Hide what it did" : "What it did, step by step", () => _showSteps = !_showSteps));
        if (_showSteps) panel.Children.Add(Steps(state));
        return panel;
    }

    private UIElement EndedBody(TurnPhaseState state)
    {
        var panel = new StackPanel();
        panel.Children.Add(Line(Icons.Stop, state.EndedReason ?? "", "TextFillColorSecondaryBrush", bold: true));
        panel.Children.Add(Toggle(_showSteps ? "Hide what it did" : "What it did, step by step", () => _showSteps = !_showSteps));
        if (_showSteps) panel.Children.Add(Steps(state));
        return panel;
    }

    private static UIElement EvidenceRow(Evidence item)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
        var glyph = Ui.Glyph(item.Kind switch
        {
            EvidenceKind.Screenshot => Icons.Camera,
            EvidenceKind.File => Icons.Document,
            EvidenceKind.RunOutput => Icons.Terminal,
            EvidenceKind.Check => Icons.CheckMark,
            _ => Icons.Info,
        }, 11, "TextFillColorSecondaryBrush");
        glyph.Width = 18;
        glyph.VerticalAlignment = VerticalAlignment.Top;
        glyph.Margin = new Thickness(0, 2, 4, 0);
        glyph.ToolTip = item.Kind.Label();
        DockPanel.SetDock(glyph, Dock.Left);
        dock.Children.Add(glyph);
        var lines = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Ui.Text(item.Title, 11.5, FontWeights.SemiBold, wrap: false));
        var at = Ui.Text("  " + PhaseText.Stamp(item.At), 10, brushKey: "TextFillColorTertiaryBrush", wrap: false);
        at.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        title.Children.Add(at);
        lines.Children.Add(title);
        if (item.Detail.Trim().Length > 0)
        {
            var detail = Ui.Text(item.Detail.Trim(), 11, brushKey: "TextFillColorSecondaryBrush");
            detail.MaxHeight = 46;
            detail.TextTrimming = TextTrimming.CharacterEllipsis;
            detail.ToolTip = item.Detail;
            lines.Children.Add(detail);
        }
        dock.Children.Add(lines);
        return dock;
    }

    /// <summary>Timestamped steps, newest first — what drilling into the badge shows.</summary>
    private static UIElement Steps(TurnPhaseState state)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var step in state.Steps.TakeLast(40).Reverse())
        {
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var time = Ui.Text(PhaseText.Stamp(step.At), 10, brushKey: "TextFillColorTertiaryBrush", wrap: false);
            time.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
            time.Width = 58;
            DockPanel.SetDock(time, Dock.Left);
            row.Children.Add(time);
            row.Children.Add(Ui.Text(step.Text, 11, brushKey: "TextFillColorSecondaryBrush"));
            panel.Children.Add(row);
        }
        return panel;
    }

    private static UIElement Line(string glyph, string text, string brush, bool bold = false)
    {
        var dock = new DockPanel();
        var icon = Ui.Glyph(glyph, 12, brush);
        icon.Margin = new Thickness(0, 0, 7, 0);
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(Ui.Text(text, 11.5, bold ? FontWeights.SemiBold : FontWeights.Normal));
        return dock;
    }

    private Button Toggle(string text, Action flip)
    {
        var button = Ui.LinkButton(text, () =>
        {
            flip();
            Redraw();
        });
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(-4, 2, 0, 0);
        if (button.Content is TextBlock label) label.FontSize = 11;
        return button;
    }

    private static Border Rule()
    {
        var rule = Ui.Divider();
        rule.Margin = new Thickness(0, 6, 0, 4);
        return rule;
    }

    internal TurnPhase? DrawnPhase => _drawnPhase;
}
