namespace Vision.Core;

// MARK: - The roster
//
// Every configured route has a role: the primary answers you (the active route), subagent servers take side
// tasks in parallel, backups take over if the primary stops answering, and the rest are just available. A route
// that is switched off has no role at all. A route can be a subagent server and a backup at once; the primary is
// never its own backup or worker.

/// <summary>Which group a route is shown in (the model menu, Settings › AI Models).</summary>
public enum RouteRole { Primary, Subagent, Backup, Other }

public static class RouteRoster
{
    /// <summary>The backups for <paramref name="primaryRouteId"/>, in the order they are tried: enabled routes marked
    /// as backups, in the order they are listed, the primary itself left out.</summary>
    public static IReadOnlyList<ProviderProfile> Backups(IEnumerable<ProviderProfile> routes, string? primaryRouteId) =>
        routes.Where(r => r.IsEnabled && r.IsBackup && r.RouteId != primaryRouteId).ToList();

    /// <summary>The role a route is shown under. (A route that is both a subagent server and a backup shows as a subagent
    /// server.)</summary>
    public static RouteRole RoleOf(ProviderProfile route, string? primaryRouteId) =>
        route.RouteId == primaryRouteId ? RouteRole.Primary
        : route.SubagentWorker ? RouteRole.Subagent
        : route.IsBackup ? RouteRole.Backup
        : RouteRole.Other;

    /// <summary>The enabled routes by role, for the model menu: Primary, Subagents, Backups, Other (each in list
    /// order; empty groups left out).</summary>
    public static IReadOnlyList<(RouteRole Role, IReadOnlyList<ProviderProfile> Routes)> Groups(IEnumerable<ProviderProfile> routes, string? primaryRouteId)
    {
        var enabled = routes.Where(r => r.IsEnabled || r.RouteId == primaryRouteId).ToList();
        return Enum.GetValues<RouteRole>()
            .Select(role => (role, (IReadOnlyList<ProviderProfile>)enabled.Where(r => RoleOf(r, primaryRouteId) == role).ToList()))
            .Where(g => g.Item2.Count > 0)
            .ToList();
    }

    public static string Title(this RouteRole role) => role switch
    {
        RouteRole.Primary => "Primary",
        RouteRole.Subagent => "Subagents",
        RouteRole.Backup => "Backups",
        _ => "Other",
    };
}
