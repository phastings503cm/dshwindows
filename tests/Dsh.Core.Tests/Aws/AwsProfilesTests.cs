namespace Dsh.Core.Tests;

public sealed class AwsProfilesTests
{
    private const string Config = """
        # Written by hand and by `aws configure`
        [default]
        region = eu-west-1
        output = json

        [profile corp-dev]
        sso_session = corp
        sso_account_id = 111122223333
        sso_role_name = Developer
        region = us-east-1

        [sso-session corp]
        sso_start_url = https://corp.awsapps.com/start
        sso_region = us-east-1

        [profile  deploy]
        role_arn = arn:aws:iam::111122223333:role/Deploy
        source_profile = default

        [profile tool]
        credential_process = "C:\Program Files\Vault\vault.exe" aws-creds --role=dev

        [profile dsh-bedrock]
        login_session = arn:aws:iam::111122223333:user/alice
        Region = eu-central-1
        s3 =
          max_concurrent_requests = 20
          addressing_style = path

        [profile keys]
        region = us-west-2

        [profile "my project"]
        region: ap-northeast-1

        [profile ci]
        web_identity_token_file = /var/run/token
        role_arn = arn:aws:iam::111122223333:role/CI

        [services local-bedrock]
        bedrock =
          endpoint_url = http://localhost:4566

        [preview]
        cloudfront = true

        ; an empty profile, ready for aws login
        [profile fresh]
        """;

    private const string Credentials = """
        [keys]
        aws_access_key_id = AKIAIOSFODNN7EXAMPLE
        aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY

        [only-in-credentials]
        aws_access_key_id = AKIAOTHER
        aws_secret_access_key = secret
        region = sa-east-1
        """;

    [Fact]
    public void ReadsProfilesAndHowEachSignsIn()
    {
        var profiles = AwsProfiles.Parse(Config, Credentials);
        Assert.Equal(["default", "corp-dev", "deploy", "tool", "dsh-bedrock", "keys", "my project", "ci", "fresh", "only-in-credentials"],
            profiles.Profiles.Select(p => p.Name));

        Assert.Equal(AwsProfileKind.Empty, profiles.Find("default")!.Kind);
        Assert.Equal(AwsProfileKind.Sso, profiles.Find("corp-dev")!.Kind);
        Assert.Equal(AwsProfileKind.AssumeRole, profiles.Find("deploy")!.Kind);
        Assert.Equal(AwsProfileKind.CredentialProcess, profiles.Find("tool")!.Kind);
        Assert.Equal(AwsProfileKind.LoginSession, profiles.Find("dsh-bedrock")!.Kind);
        Assert.Equal(AwsProfileKind.AccessKeys, profiles.Find("keys")!.Kind);
        Assert.Equal(AwsProfileKind.WebIdentity, profiles.Find("ci")!.Kind);
        Assert.Equal(AwsProfileKind.Empty, profiles.Find("fresh")!.Kind);
        Assert.Equal(AwsProfileKind.AccessKeys, profiles.Find("only-in-credentials")!.Kind);

        Assert.Null(profiles.Find("corp"));      // an sso-session, not a profile
        Assert.Null(profiles.Find("preview"));   // not a profile section
        Assert.Null(profiles.Find("DSH-BEDROCK")); // names are case-sensitive
    }

    [Fact]
    public void ReadsRegionsAndSettings()
    {
        var profiles = AwsProfiles.Parse(Config, Credentials);
        Assert.Equal("us-east-1", profiles.Find("corp-dev")!.Region);
        Assert.Equal("eu-central-1", profiles.Find("dsh-bedrock")!.Region); // keys are case-insensitive ("Region")
        Assert.Equal("us-west-2", profiles.Find("keys")!.Region);           // config and credentials merge
        Assert.Equal("ap-northeast-1", profiles.Find("my project")!.Region); // quoted name, ":" separator
        Assert.Equal("sa-east-1", profiles.Find("only-in-credentials")!.Region);
        Assert.Null(profiles.Find("fresh")!.Region);
        Assert.Equal("\"C:\\Program Files\\Vault\\vault.exe\" aws-creds --role=dev", profiles.Find("tool")!.Settings["credential_process"]);
        Assert.Equal("max_concurrent_requests = 20\naddressing_style = path", profiles.Find("dsh-bedrock")!.Settings["s3"]);
        Assert.Equal("AKIAIOSFODNN7EXAMPLE", profiles.Find("keys")!.Settings["aws_access_key_id"]);
    }

    [Fact]
    public void OnlyEmptyAndLoginProfilesCanUseAwsLogin()
    {
        var profiles = AwsProfiles.Parse(Config, Credentials);
        Assert.Equal(["default", "dsh-bedrock", "my project", "fresh"], profiles.Profiles.Where(p => p.CanUseAwsLogin).Select(p => p.Name));
        Assert.Equal("IAM Identity Center (SSO)", profiles.Find("corp-dev")!.KindDescription);
        Assert.Equal("access keys", profiles.Find("keys")!.KindDescription);
    }

    [Fact]
    public void CredentialsOverrideConfig()
    {
        var profiles = AwsProfiles.Parse("[profile p]\nregion = us-east-1\naws_access_key_id = FROMCONFIG\n", "[p]\naws_access_key_id = FROMCREDENTIALS\n");
        Assert.Equal("FROMCREDENTIALS", profiles.Find("p")!.Settings["aws_access_key_id"]);
        Assert.Equal("us-east-1", profiles.Find("p")!.Region);
    }

    [Fact]
    public void DefaultRegionComesFromTheEnvironmentFirst()
    {
        Assert.Equal("eu-west-1", AwsProfiles.Parse(Config, null).DefaultRegion);
        var env = new Dictionary<string, string?> { ["AWS_DEFAULT_REGION"] = "ap-south-1" };
        Assert.Equal("ap-south-1", AwsProfiles.Parse(Config, null, env.GetValueOrDefault).DefaultRegion);
        env["AWS_REGION"] = " us-east-2 ";
        Assert.Equal("us-east-2", AwsProfiles.Parse(Config, null, env.GetValueOrDefault).DefaultRegion);
        Assert.Null(AwsProfiles.Parse(null, null).DefaultRegion);
    }

    [Fact]
    public void EmptyOrOddFilesDontBreakIt()
    {
        Assert.Empty(AwsProfiles.Parse(null, null).Profiles);
        Assert.Empty(AwsProfiles.Parse("", "").Profiles);
        var odd = AwsProfiles.Parse("\uFEFFregion = orphan\n[profile ok]\r\nregion = us-east-1\r\n[profile \"unterminated]\nregion = x\n[profile a b]\nregion = y\n", null);
        Assert.Equal(["ok"], odd.Profiles.Select(p => p.Name));
        Assert.Equal("us-east-1", odd.Find("ok")!.Region);
    }

    [Fact]
    public void FindsTheFilesTheCliUses()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, ".aws", "config"), AwsProfiles.ConfigPath(_ => null));
        Assert.Equal(Path.Combine(home, ".aws", "credentials"), AwsProfiles.CredentialsPath(_ => null));
        Assert.Equal("/etc/aws/config", AwsProfiles.ConfigPath(n => n == "AWS_CONFIG_FILE" ? "/etc/aws/config" : null));
        Assert.Equal(Path.Combine(home, "creds"), AwsProfiles.CredentialsPath(n => n == "AWS_SHARED_CREDENTIALS_FILE" ? "~/creds" : null));
    }

    [Fact]
    public void LoadsFromTheFilesTheEnvironmentNames()
    {
        using var dir = new TempDirectory("dsh-aws-profiles");
        var config = dir.Write("config", Config);
        var credentials = dir.Write("credentials", Credentials);
        var env = new Dictionary<string, string?> { ["AWS_CONFIG_FILE"] = config, ["AWS_SHARED_CREDENTIALS_FILE"] = credentials };
        var profiles = AwsProfiles.Load(env.GetValueOrDefault);
        Assert.Equal(10, profiles.Profiles.Count);
        Assert.Equal(config, profiles.ConfigFile);
        Assert.Equal(credentials, profiles.CredentialsFile);

        env["AWS_CONFIG_FILE"] = dir["missing-config"];
        Assert.Equal(["keys", "only-in-credentials"], AwsProfiles.Load(env.GetValueOrDefault).Profiles.Select(p => p.Name));
    }
}
