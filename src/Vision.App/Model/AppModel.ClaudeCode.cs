using System.Windows;
using System.Windows.Threading;

namespace Vision.App.Model;

public sealed partial class AppModel
{
    private ClaudeCodeService? _claudeCode;

    /// <summary>Claude Code on this computer: the claude CLI's status and the model-menu routes that put chats on it.</summary>
    public ClaudeCodeService ClaudeCode => _claudeCode ??= new ClaudeCodeService(Config, Host, Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);

    /// <summary>Open Settings › Claude Code.</summary>
    public void ShowClaudeCode() => ShowSettings(SettingsTab.ClaudeCode);
}
