using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vision.App.Model;
using Vision.Core;

namespace Vision.App.Infrastructure;

/// <summary>Vision was called DSH before 0.9. This brings the data folder and Credential Manager entries of DSH (the
/// app's former name) over to Vision's, the same way at every start, and backs <c>Vision.exe --migrate-only</c>:
/// do just that, without a window, and report what Vision then reads back.</summary>
public static class LegacyUpgrade
{
    public const string MigrateOnlyFlag = "--migrate-only";

    /// <summary>The move still to make, or null when there is none: the user chose the folder (VISION_HOME, or the
    /// legacy DSH_HOME), a portable copy has no portable DSH beside it, or it was brought over already.</summary>
    public static DataFolderMigration? Pending()
    {
        if (AppPaths.Overridden is not null) return null;
        var program = AppContext.BaseDirectory;
        DataFolderMigration migration;
        if (AppPaths.PortableRootFor(program) is { } portable)
        {
            // A portable copy unpacked beside a portable DSH copies DSH's data (that copy keeps its own).
            if (AppPaths.LegacyPortableRootFor(program) is not { } legacyPortable) return null;
            migration = new DataFolderMigration(legacyPortable, portable, allowMove: false);
        }
        else
        {
            migration = new DataFolderMigration(AppPaths.LegacyDefaultRoot, AppPaths.DefaultRoot);
        }
        return migration.IsPending ? migration : null;
    }

    /// <summary>Bring the data over, and point a portable DSH's "Open with DSH" at Vision once it is.</summary>
    public static DataMigrationOutcome Run(DataFolderMigration migration)
    {
        var outcome = migration.Run();
        if (outcome.BroughtOver)
        {
            try { ShellIntegration.ReplaceLegacyFolderMenu(); } catch (Exception) { }
        }
        return outcome;
    }

    /// <summary>The path after <c>--migrate-only</c>, "" when the flag has none (the report goes to standard
    /// output), or null when the flag isn't there.</summary>
    public static string? ParseMigrateOnly(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals(MigrateOnlyFlag, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        return index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : "";
    }

    /// <summary>Exit codes of <c>--migrate-only</c>.</summary>
    public static class ExitCodes
    {
        /// <summary>Brought over (or already, or there was nothing to bring), and everything reads back.</summary>
        public const int Done = 0;
        /// <summary>The data couldn't be brought over, or something in it doesn't read back. The report says what.</summary>
        public const int Failed = 1;
        /// <summary>DSH is still open: close it and run again.</summary>
        public const int LegacyAppRunning = 2;
        /// <summary>Vision is open (it brought the data over when it started).</summary>
        public const int AppRunning = 3;
    }

    /// <summary><c>Vision.exe --migrate-only [&lt;report.json&gt;]</c>: what the first start does with DSH's data folder
    /// and Credential Manager entries, without a window or a dialog, for scripts that upgrade many PCs and for the
    /// CI upgrade test (scripts/installer-test.ps1). Then it opens the data the way the app does — settings,
    /// conversations, the task queue, the credential vault — and reports counts only, never a value.</summary>
    public static int RunMigrateOnly(string reportPath)
    {
        var report = new MigrateOnlyReport { Version = AppInfo.Version };
        try
        {
            using var instance = new SingleInstance(SingleInstance.AppId);
            if (!instance.IsFirst)
                return Finish(report, ExitCodes.AppRunning, "Vision is open; it brings DSH's data over itself when it starts.", reportPath);
            if (SingleInstance.IsRunning(SingleInstance.LegacyAppId))
                return Finish(report, ExitCodes.LegacyAppRunning, "DSH is still open. Close it, then run this again.", reportPath);

            var exitCode = ExitCodes.Done;
            if (Pending() is { } migration)
            {
                var outcome = Run(migration);
                report.Result = outcome.Result.ToString();
                report.From = outcome.From;
                report.To = outcome.To;
                report.Problem = outcome.Problem;
                report.SetAside = outcome.SetAside;
                report.RewrittenFiles = outcome.RewrittenFiles;
                report.SkippedLinks = outcome.SkippedLinks;
                if (outcome.Result == DataMigrationResult.Failed)
                {
                    exitCode = ExitCodes.Failed;
                    // A move stopped half-way: the next run finishes it; nothing may be opened meanwhile.
                    if (outcome.DataFolder is null) return Finish(report, exitCode, null, reportPath);
                    // Nothing moved: the app would run on DSH's folder this time, so read that back.
                    AppPaths.SessionRoot = outcome.DataFolder;
                }
            }
            else
            {
                report.Result = AppPaths.Overridden is not null ? "FolderChosen"
                    : File.Exists(Path.Combine(AppPaths.Root, DataFolderMigration.MarkerFileName))
                        ? nameof(DataMigrationResult.AlreadyDone)
                        : nameof(DataMigrationResult.NothingToDo);
            }
            report.DataFolder = AppPaths.Root;

            var config = new AppConfig();
            report.KeysBroughtOver = config.BringLegacySecretsOver();
            config.SaveNow();
            if (!ReadBack(report, config) && exitCode == ExitCodes.Done) exitCode = ExitCodes.Failed;
            return Finish(report, exitCode, null, reportPath);
        }
        catch (Exception error)
        {
            // The message names a file or a step, never a secret (the stores never put one in an exception).
            return Finish(report, ExitCodes.Failed, $"{error.GetType().Name}: {error.Message}", reportPath);
        }
    }

    /// <summary>Open the data with the app's own readers and count what is there. False when something that is there
    /// can't be read (a vault value that doesn't decrypt).</summary>
    private static bool ReadBack(MigrateOnlyReport report, AppConfig config)
    {
        var readBack = new MigrateOnlyReadBack
        {
            Routes = config.Providers.Count,
            RoutesWithKeys = config.Providers.Count(p => !string.IsNullOrEmpty(SafeRead(SecretStore.ProviderTarget(p.RouteId)))),
        };
        var log = new ConversationLog();
        var sessions = log.List();
        readBack.Conversations = sessions.Count;
        readBack.ConversationItems = sessions.Sum(s => log.LoadItems(s.Id).Count);
        var ok = true;
        var queue = new TaskQueue(TaskQueue.DefaultFilePath);
        readBack.QueueTasks = queue.Count;
        if (queue.LoadProblem is { } queueProblem)
        {
            readBack.QueueProblem = queueProblem;
            ok = false;
        }

        var vault = new CredentialVault();
        var entries = vault.All;
        readBack.VaultEntries = entries.Count;
        try
        {
            readBack.VaultValuesReadable = entries.Count(e => vault.Value(e.Id) is not null);
            if (readBack.VaultValuesReadable != entries.Count) ok = false;
        }
        catch (VaultException error)
        {
            readBack.VaultProblem = error.Message;
            ok = false;
        }
        report.ReadBack = readBack;
        return ok;
    }

    private static string? SafeRead(string target)
    {
        try { return SecretStore.Read(target); } catch (Exception) { return null; }
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private static int Finish(MigrateOnlyReport report, int exitCode, string? problem, string reportPath)
    {
        report.ExitCode = exitCode;
        if (problem is not null) report.Problem = problem;
        var text = JsonSerializer.Serialize(report, Json);
        try
        {
            if (reportPath.Length > 0)
            {
                var full = Path.GetFullPath(reportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text);
            }
            else
            {
                Console.WriteLine(text);
            }
        }
        catch (Exception)
        {
            // The exit code still says how it went.
        }
        return exitCode;
    }
}

/// <summary>What <c>--migrate-only</c> did and read back. Counts and paths only — never a key or a vault value.</summary>
public sealed class MigrateOnlyReport
{
    public string Version { get; set; } = "";
    /// <summary>A <see cref="DataMigrationResult"/>, or "FolderChosen" when VISION_HOME (or DSH_HOME) is set.</summary>
    public string? Result { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    /// <summary>The folder Vision runs on after this.</summary>
    public string? DataFolder { get; set; }
    public string? Problem { get; set; }
    public string? SetAside { get; set; }
    public int RewrittenFiles { get; set; }
    public IReadOnlyList<string>? SkippedLinks { get; set; }
    /// <summary>Credential Manager entries moved from DSH's names to Vision's this run.</summary>
    public int KeysBroughtOver { get; set; }
    public MigrateOnlyReadBack? ReadBack { get; set; }
    public int ExitCode { get; set; }
}

public sealed class MigrateOnlyReadBack
{
    public int Routes { get; set; }
    /// <summary>Routes whose key Vision finds under its own Credential Manager name.</summary>
    public int RoutesWithKeys { get; set; }
    public int Conversations { get; set; }
    public int ConversationItems { get; set; }
    public int QueueTasks { get; set; }
    /// <summary>Why the task queue file couldn't be read (it was set aside, and the queue starts empty).</summary>
    public string? QueueProblem { get; set; }
    public int VaultEntries { get; set; }
    /// <summary>Vault values that decrypt (the vault's encryption is tied to constants that must never change).</summary>
    public int VaultValuesReadable { get; set; }
    public string? VaultProblem { get; set; }
}
