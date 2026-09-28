using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dsh.Core;

namespace Dsh.App.Views.Skills;

/// <summary>Small shared pieces for showing skills: coloured badges for where a skill came from and
/// how it behaves.</summary>
public static class SkillVisuals
{
    public static Color OriginColor(SkillOrigin origin) => origin switch
    {
        SkillOrigin.Claude => Color.FromRgb(0xD9, 0x77, 0x06),
        SkillOrigin.Cursor => Color.FromRgb(0x0D, 0x94, 0x88),
        SkillOrigin.Agents => Color.FromRgb(0x63, 0x66, 0xF1),
        SkillOrigin.Qwen => Color.FromRgb(0x93, 0x33, 0xEA),
        SkillOrigin.Builtin => Color.FromRgb(0x80, 0x80, 0x80),
        _ => Color.FromRgb(0x00, 0x78, 0xD4),
    };

    public static Border Badge(string text, Color? tint = null)
    {
        var color = tint ?? Color.FromRgb(0x80, 0x80, 0x80);
        var label = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
        };
        return new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(0, 0, 4, 2),
            Background = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B)),
        };
    }

    public static WrapPanel Badges(Skill skill, bool includeScope = true)
    {
        var panel = new WrapPanel();
        panel.Children.Add(Badge(skill.Origin.Label(), OriginColor(skill.Origin)));
        if (skill.Kind != SkillKind.Skill) panel.Children.Add(Badge(skill.Kind.Label()));
        if (includeScope && skill.Origin != SkillOrigin.Builtin)
            panel.Children.Add(Badge(skill.Scope == SkillScope.Project ? "Project" : "Everywhere"));
        if (skill.AlwaysApply) panel.Children.Add(Badge("Always on", Color.FromRgb(0x10, 0x7C, 0x10)));
        if (skill.Globs.Count > 0) panel.Children.Add(Badge("Files: " + skill.Globs[0]));
        if (!skill.ModelInvocable && !skill.AlwaysApply) panel.Children.Add(Badge("You run it"));
        if (skill.Shadowed) panel.Children.Add(Badge("Overridden", Color.FromRgb(0xCA, 0x50, 0x10)));
        return panel;
    }
}
