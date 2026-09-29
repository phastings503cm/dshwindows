namespace Dsh.Core;

// MARK: - AWS CLI profiles
//
// What is already set up in %USERPROFILE%\.aws\config and \credentials, read the way botocore reads it
// (configloader.build_profile_map): "[default]" and "[profile NAME]" sections in config, bare "[NAME]"
// sections in credentials, credentials values layered over config values. DSH only ever reads these
// files; anything it changes goes through the CLI (`aws login`, `aws configure set`), which knows how to
// rewrite them safely.

/// <summary>How a profile gets its credentials, in the order `aws login` checks them
/// (awscli/customizations/login/login.py).</summary>
public enum AwsProfileKind
{
    /// <summary>No credentials configured yet (maybe just a Region) — `aws login` can use it.</summary>
    Empty,
    /// <summary>Signed in with `aws login` (login_session).</summary>
    LoginSession,
    /// <summary>IAM Identity Center (sso_session, or the older sso_start_url/sso_account_id/sso_role_name).</summary>
    Sso,
    /// <summary>Long-term access keys (aws_access_key_id).</summary>
    AccessKeys,
    /// <summary>Assumes a role (role_arn) from another profile or source.</summary>
    AssumeRole,
    /// <summary>Runs a program for credentials (credential_process).</summary>
    CredentialProcess,
    /// <summary>A web identity token file (web_identity_token_file).</summary>
    WebIdentity,
}

/// <summary>One profile: its name, how it signs in, its Region, and every setting (lower-case keys).</summary>
public sealed record AwsProfile(string Name, AwsProfileKind Kind, string? Region, IReadOnlyDictionary<string, string> Settings)
{
    /// <summary>"Signed in with aws login", "IAM Identity Center (SSO)", ...</summary>
    public string KindDescription => Describe(Kind);

    /// <summary>`aws login` refuses profiles that already have another kind of credentials.</summary>
    public bool CanUseAwsLogin => Kind is AwsProfileKind.Empty or AwsProfileKind.LoginSession;

    public static string Describe(AwsProfileKind kind) => kind switch
    {
        AwsProfileKind.LoginSession => "signed in with the browser (aws login)",
        AwsProfileKind.Sso => "IAM Identity Center (SSO)",
        AwsProfileKind.AccessKeys => "access keys",
        AwsProfileKind.AssumeRole => "an assumed role",
        AwsProfileKind.CredentialProcess => "a credential program (credential_process)",
        AwsProfileKind.WebIdentity => "a web identity token",
        _ => "no credentials yet",
    };
}

/// <summary>The profiles in the AWS CLI's config and credentials files.</summary>
public sealed class AwsProfiles
{
    private readonly Dictionary<string, AwsProfile> _byName;

    private AwsProfiles(IReadOnlyList<AwsProfile> profiles, string? defaultRegion, string? configFile, string? credentialsFile)
    {
        Profiles = profiles;
        DefaultRegion = defaultRegion;
        ConfigFile = configFile;
        CredentialsFile = credentialsFile;
        _byName = profiles.ToDictionary(p => p.Name, StringComparer.Ordinal);
    }

    /// <summary>Every profile, config file order first, then any only in the credentials file.</summary>
    public IReadOnlyList<AwsProfile> Profiles { get; }

    /// <summary>The Region the CLI uses when none is given: AWS_REGION, then AWS_DEFAULT_REGION, then
    /// the default profile's region.</summary>
    public string? DefaultRegion { get; }

    public string? ConfigFile { get; }
    public string? CredentialsFile { get; }

    /// <summary>Profile names are case-sensitive, as in the CLI.</summary>
    public AwsProfile? Find(string name) => _byName.GetValueOrDefault(name);

    /// <summary>The config file: AWS_CONFIG_FILE, else ~/.aws/config (on Windows ~ is %USERPROFILE%).</summary>
    public static string ConfigPath(Func<string, string?>? environment = null) =>
        Resolve(environment ?? Environment.GetEnvironmentVariable, "AWS_CONFIG_FILE", "config");

    /// <summary>The credentials file: AWS_SHARED_CREDENTIALS_FILE, else ~/.aws/credentials.</summary>
    public static string CredentialsPath(Func<string, string?>? environment = null) =>
        Resolve(environment ?? Environment.GetEnvironmentVariable, "AWS_SHARED_CREDENTIALS_FILE", "credentials");

    private static string Resolve(Func<string, string?> environment, string variable, string file)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (environment(variable) is { Length: > 0 } custom)
        {
            var expanded = Environment.ExpandEnvironmentVariables(custom.Trim());
            if (expanded == "~" || expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
                expanded = Path.Combine(home, expanded.Length > 2 ? expanded[2..] : "");
            return expanded;
        }
        return Path.Combine(home, ".aws", file);
    }

    /// <summary>Read this machine's files. Missing or unreadable files count as empty.</summary>
    public static AwsProfiles Load(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var config = ConfigPath(environment);
        var credentials = CredentialsPath(environment);
        return Parse(ReadOrNull(config), ReadOrNull(credentials), environment, config, credentials);
    }

    private static string? ReadOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Parse the two files' text (either may be null).</summary>
    public static AwsProfiles Parse(string? configText, string? credentialsText, Func<string, string?>? environment = null,
                                    string? configFile = null, string? credentialsFile = null)
    {
        environment ??= _ => null;
        var order = new List<string>();
        var settings = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        void Merge(string name, Dictionary<string, string> values)
        {
            if (!settings.TryGetValue(name, out var existing))
            {
                settings[name] = existing = new Dictionary<string, string>(StringComparer.Ordinal);
                order.Add(name);
            }
            foreach (var (key, value) in values) existing[key] = value;
        }

        foreach (var (section, values) in ParseIni(configText))
        {
            if (section == "default") Merge("default", values);
            else if (section.StartsWith("profile", StringComparison.Ordinal) && SplitSection(section) is ["profile", var name]) Merge(name, values);
        }
        foreach (var (section, values) in ParseIni(credentialsText)) Merge(section, values);

        var profiles = order.Select(name => Build(name, settings[name])).ToList();
        var defaultRegion = FirstNonEmpty(environment("AWS_REGION"), environment("AWS_DEFAULT_REGION"),
            settings.TryGetValue("default", out var d) ? d.GetValueOrDefault("region") : null);
        return new AwsProfiles(profiles, defaultRegion, configFile, credentialsFile);
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static AwsProfile Build(string name, Dictionary<string, string> values)
    {
        bool Has(string key) => values.TryGetValue(key, out var v) && v.Length > 0;
        var kind = Has("web_identity_token_file") ? AwsProfileKind.WebIdentity
            : Has("sso_session") || Has("sso_role_name") || Has("sso_account_id") || Has("sso_start_url") ? AwsProfileKind.Sso
            : Has("aws_access_key_id") ? AwsProfileKind.AccessKeys
            : Has("role_arn") ? AwsProfileKind.AssumeRole
            : Has("credential_process") ? AwsProfileKind.CredentialProcess
            : Has("login_session") ? AwsProfileKind.LoginSession
            : AwsProfileKind.Empty;
        return new AwsProfile(name, kind, FirstNonEmpty(values.GetValueOrDefault("region")), values);
    }

    /// <summary>"profile  my-name" → ["profile", "my-name"]; quotes group words, as shlex does.</summary>
    private static string[] SplitSection(string section)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        var any = false;
        foreach (var c in section)
        {
            if (quote is { } q)
            {
                if (c == q) quote = null;
                else current.Append(c);
                continue;
            }
            if (c is '"' or '\'')
            {
                quote = c;
                any = true;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0 || any) parts.Add(current.ToString());
                current.Clear();
                any = false;
                continue;
            }
            current.Append(c);
        }
        if (quote is not null) return [];
        if (current.Length > 0 || any) parts.Add(current.ToString());
        return [.. parts];
    }

    /// <summary>INI as Python's RawConfigParser reads it: "#" and ";" start comment lines, "=" or ":"
    /// separates key and value, keys are lower-cased, and indented lines continue the previous value
    /// (botocore uses those for nested settings such as "s3 =", which DSH doesn't need, so they are
    /// kept as text).</summary>
    public static IEnumerable<(string Section, Dictionary<string, string> Values)> ParseIni(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        string? section = null;
        Dictionary<string, string>? values = null;
        string? lastKey = null;
        foreach (var rawLine in TextUtil.NormalizeNewlines(text.TrimStart('﻿')).Split('\n'))
        {
            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or ';') continue;
            if (trimmed[0] == '[' && trimmed.EndsWith(']'))
            {
                if (section is not null) yield return (section, values!);
                section = trimmed[1..^1].Trim();
                values = new Dictionary<string, string>(StringComparer.Ordinal);
                lastKey = null;
                continue;
            }
            if (values is null) continue;
            if (char.IsWhiteSpace(rawLine[0]) && lastKey is not null)
            {
                values[lastKey] = (values[lastKey] + "\n" + trimmed).Trim();
                continue;
            }
            var equals = trimmed.IndexOf('=');
            var colon = trimmed.IndexOf(':');
            var split = equals < 0 ? colon : colon < 0 ? equals : Math.Min(equals, colon);
            if (split <= 0) continue;
            lastKey = trimmed[..split].Trim().ToLowerInvariant();
            values[lastKey] = trimmed[(split + 1)..].Trim();
        }
        if (section is not null) yield return (section, values!);
    }
}
