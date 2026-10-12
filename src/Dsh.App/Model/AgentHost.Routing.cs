using System.Windows.Threading;
using Dsh.Core;

namespace Dsh.App.Model;

// MARK: - Which model a chat talks to, and what it may see
//
// The roster around the primary model: backups that take a turn over when the primary stops answering, the
// sensitive-data shield on every route that isn't private (its report shows as a chip on the reply), what the main
// agent is told about the subagent servers and their privacy, and the plans whose terms allow only interactive use.

public sealed partial class AgentHost
{
    /// <summary>Said once per chat when a picture goes to a model that isn't private.</summary>
    public const string ImagesNotice = "Images are sent as they are — DSH can't check pictures for sensitive details.";

    /// <summary>Chat → what the shield masked in the request whose reply is about to be shown.</summary>
    private readonly Dictionary<string, ShieldReport> _pendingShield = new();
    /// <summary>Chats whose current turn already said it ran on a backup.</summary>
    private readonly HashSet<string> _failoverNoted = new();
    private IReadOnlyList<PluginManifest>? _userPlugins;

    /// <summary>The user's own plugins (a chat without a project gets no project ones).</summary>
    private IReadOnlyList<PluginManifest> UserPlugins() => _userPlugins ??= PluginLoader.Load(null).Plugins;

    /// <summary>The shield's settings now (null when it is switched off), reporting to <paramref name="sessionId"/>'s chat.</summary>
    private ShieldOptions? ShieldFor(string? sessionId)
    {
        var privacy = Config.Privacy;
        IReadOnlyList<string> vault;
        try
        {
            vault = Vault.ValuesForRedaction().Select(v => v.Value).ToList();
        }
        catch (Exception)
        {
            vault = [];
        }
        // The vault's secrets are masked even with the switch off (as in DSH for macOS); with it off and an empty
        // vault there is nothing to do.
        if (!privacy.Mask && vault.Count == 0) return null;
        return new ShieldOptions
        {
            Categories = privacy.Mask ? privacy.Categories : SensitiveCategories.None,
            CustomTerms = privacy.Mask ? privacy.TermLines : [],
            VaultSecrets = vault,
            Report = sessionId is null ? null : report => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => OnShieldReport(sessionId, report)),
        };
    }

    /// <summary>The client a chat's own turns use: the primary, and — when there are backups — the backups behind it,
    /// each with its own shield. Subagents never get backups (the fleet moves their tasks itself).</summary>
    private IProviderClient MakeChatClient(ProviderProfile profile, string sessionId)
    {
        var primary = MakeClient(profile, sessionId);
        var backups = BackupRoutes();
        if (backups.Count == 0) return primary;
        return new FailoverClient(primary, backups.Select(b => MakeClient(b, sessionId)).ToList(),
            notice => _dispatcher.BeginInvoke(DispatcherPriority.Normal, () => OnFailover(sessionId, notice)));
    }

    /// <summary>The enabled backups of the primary, in list order, with their keys.</summary>
    private IReadOnlyList<ProviderProfile> BackupRoutes() =>
        RouteRoster.Backups(Config.Providers, Config.ActiveRoute).Select(route =>
        {
            var copy = route.DeepCopy();
            var key = Config.ApiKey(route);
            copy.ApiKey = key.Length == 0 ? null : key;
            return copy;
        }).ToList();

    private void OnShieldReport(string sessionId, ShieldReport report)
    {
        if (Session(sessionId) is not { } vm) return;
        if (report.HadImages && !vm.Entries.OfType<MessageEntryVM>().Any(m => m.Text == ImagesNotice))
        {
            vm.Note(ImagesNotice);
            Log.RecordItem(sessionId, "notice", ImagesNotice);
        }
        if (report.Total > 0) _pendingShield[sessionId] = report;
        else _pendingShield.Remove(sessionId);
    }

    private void OnFailover(string sessionId, FailoverNotice notice)
    {
        if (Session(sessionId) is not { } vm || !_failoverNoted.Add(sessionId)) return;
        vm.Note(notice.Text);
        Log.RecordItem(sessionId, "notice", notice.Text);
    }

    /// <summary>Changes when what a chat's engine is built with around the model does (the shield's settings, the backups,
    /// the subagent servers and their privacy), so the engine is rebuilt exactly then.</summary>
    private string RoutingStamp()
    {
        var backups = string.Join(",", RouteRoster.Backups(Config.Providers, Config.ActiveRoute).Select(b => b.RouteId + (b.IsPrivateRoute ? "+p" : "")));
        var servers = string.Join(",", SubagentServersNow().Select(s => s.Label + (s.IsPrivate ? "+p" : "")));
        var primary = Config.Providers.FirstOrDefault(p => p.RouteId == Config.ActiveRoute)?.IsPrivateRoute;
        return $"{Config.Privacy}|{primary}|{backups}|{servers}";
    }

    /// <summary>The subagent servers (workers) as the model is told about them, with their privacy.</summary>
    private IReadOnlyList<SubagentServer> SubagentServersNow() =>
        WorkerRoutes.Select(r => new SubagentServer(RouteLabel(r), r.IsPrivateRoute)).ToList();

    // MARK: Interactive-only plans

    /// <summary>The primary route, when its plan's terms allow only interactive use (the Qwen Coding Plan).</summary>
    public ProviderProfile? InteractiveOnlyPrimary =>
        Config.Providers.FirstOrDefault(p => p.RouteId == Config.ActiveRoute) is { IsInteractiveOnly: true } primary ? primary : null;

    public static string QueueRefusal(ProviderProfile route) =>
        $"{route.Name} is for interactive use only under its plan's terms, so the queue won't run on it. Make another model primary for unattended work.";

    public static string GoalRefusal(ProviderProfile route) =>
        $"{route.Name} is for interactive use only under its plan's terms, so the goal won't carry on by itself. Reply to take it one round further, or make another model primary for unattended work.";
}
