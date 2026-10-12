namespace Dsh.Core.Tests;

/// <summary>The detectors behind the privacy shield. DSH for macOS runs the same cases.</summary>
public sealed class SensitiveDataTests
{
    private static readonly SensitiveDetector All = new(SensitiveCategories.All);
    private static readonly SensitiveDetector Defaults = new(SensitiveCategories.Default);

    private static List<(string Value, SensitiveKind Kind)> Found(string text, SensitiveDetector? detector = null) =>
        (detector ?? All).Find(text).Select(m => (text.Substring(m.Start, m.Length), m.Kind)).ToList();

    private static void Masks(string text, string value, SensitiveKind kind, SensitiveDetector? detector = null) =>
        Assert.Contains((value, kind), Found(text, detector));

    // MARK: Secrets

    public static TheoryData<string, string> Secrets() => new()
    {
        { "key AKIAIOSFODNN7EXAMPLE here", "AKIAIOSFODNN7EXAMPLE" },
        { "ASIAZZZZZZZZZZZZZZZZ", "ASIAZZZZZZZZZZZZZZZZ" },
        { "token ghp_" + new string('a', 36), "ghp_" + new string('a', 36) },
        { "gho_" + new string('B', 40), "gho_" + new string('B', 40) },
        { "github_pat_" + new string('x', 60), "github_pat_" + new string('x', 60) },
        { "OPENAI sk-proj-" + new string('A', 30), "sk-proj-" + new string('A', 30) },
        { "sk-" + new string('z', 24), "sk-" + new string('z', 24) },
        { "sk-ant-api03-" + new string('Q', 30), "sk-ant-api03-" + new string('Q', 30) },
        { "slack xoxb-1234567890-abcdef", "xoxb-1234567890-abcdef" },
        { "AIza" + new string('S', 35), "AIza" + new string('S', 35) },
        { "sk_live_" + new string('9', 20), "sk_live_" + new string('9', 20) },
        { "rk_test_" + new string('r', 16), "rk_test_" + new string('r', 16) },
        { "sst_1a2b3c4d_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ", "sst_1a2b3c4d_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQ" },
        { "jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U",
          "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U" },
        { "Authorization: Bearer abcdefghijklmnop1234", "abcdefghijklmnop1234" },
        { "postgres://admin:Hunter2xyz@db:5432/app", "Hunter2xyz" },
        { "password = s3cr3tValue", "s3cr3tValue" },
        { "API_KEY: \"zx81-really-long\"", "zx81-really-long" },
        { "db_pwd='P4ssw0rd!'", "P4ssw0rd!" },
        { "Access-Key=ABCDEFGH12345", "ABCDEFGH12345" },
    };

    [Theory]
    [MemberData(nameof(Secrets))]
    public void SecretsAreFound(string text, string secret) => Masks(text, secret, SensitiveKind.Secret);

    [Fact]
    public void APrivateKeyBlockIsMaskedWhole()
    {
        var block = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQ==\nAAAA\n-----END OPENSSH PRIVATE KEY-----";
        Masks($"key:\n{block}\nthanks", block, SensitiveKind.Secret);
        // Cut off before its END line: the body is still covered.
        var cut = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEA1234\nabcd";
        Assert.Equal(cut, Found("x " + cut).Single().Value);
    }

    [Theory]
    [InlineData("password = ***")]
    [InlineData("password = <your password>")]
    [InlineData("token: ${GITHUB_TOKEN}")]
    [InlineData("api_key=xxxxxxxxxx")]
    [InlineData("password: changeme")]
    [InlineData("api-key = your-api-key-here")]
    [InlineData("postgres://user:***@db/app")]
    [InlineData("token=short")]                 // under six characters
    [InlineData("the password is required")]    // no separator
    [InlineData("max_tokens: 4096")]
    [InlineData("echo token=[vault:DEPLOY_TOKEN]")] // the vault's own stand-ins
    [InlineData("$env:OPENAI_API_KEY = \"{{vault:OPENAI_API_KEY}}\"")]
    public void PlaceholdersAndProseAreNotSecrets(string text) => Assert.DoesNotContain(Found(text), f => f.Kind == SensitiveKind.Secret);

    // MARK: Email, phone

    [Fact]
    public void EmailsAreFound()
    {
        Masks("Write to pat.hastings+dsh@example.co.uk today", "pat.hastings+dsh@example.co.uk", SensitiveKind.Email);
        Assert.Empty(Found("not an email: user@localhost or @handle"));
    }

    [Theory]
    [InlineData("call +14155550123 now", "+14155550123")]
    [InlineData("(415) 555-0123", "(415) 555-0123")]
    [InlineData("415.555.0123", "415.555.0123")]
    [InlineData("415-555-0123", "415-555-0123")]
    public void PhoneNumbersAreFound(string text, string phone) => Masks(text, phone, SensitiveKind.Phone);

    [Theory]
    [InlineData("const x = 4155550123;")]
    [InlineData("port 8080 and 1234567")]
    public void PlainNumbersInCodeAreNotPhones(string text) => Assert.DoesNotContain(Found(text), f => f.Kind == SensitiveKind.Phone);

    // MARK: IDs and cards

    [Fact]
    public void SocialSecurityNumbersAreFound() => Masks("SSN 078-05-1120.", "078-05-1120", SensitiveKind.Id);

    [Theory]
    [InlineData("4111 1111 1111 1111", true)]
    [InlineData("4111-1111-1111-1111", true)]
    [InlineData("4111111111111111", true)]
    [InlineData("378282246310005", true)]            // Amex, 15 digits
    [InlineData("4111 1111 1111 1112", false)]       // fails Luhn
    [InlineData("1234567890123", false)]
    public void CardNumbersNeedToPassLuhn(string card, bool expected)
    {
        var found = Found($"card: {card} thanks");
        Assert.Equal(expected, found.Contains((card, SensitiveKind.Card)));
        if (!expected) Assert.DoesNotContain(found, f => f.Kind == SensitiveKind.Card);
    }

    [Fact]
    public void ACardBesideOtherDigitsIsStillFound() => Masks("4111 1111 1111 1111 2024", "4111 1111 1111 1111", SensitiveKind.Card);

    [Fact]
    public void LuhnAndIbanChecks()
    {
        Assert.True(SensitiveDetector.Luhn("79927398713"));
        Assert.False(SensitiveDetector.Luhn("79927398710"));
        Assert.True(SensitiveDetector.IbanValid("DE89370400440532013000"));
        Assert.True(SensitiveDetector.IbanValid("GB82WEST12345698765432"));
        Assert.False(SensitiveDetector.IbanValid("DE89370400440532013001"));
    }

    [Fact]
    public void IbansAreFoundOnlyWhenTheirCheckDigitsHold()
    {
        Masks("pay to DE89370400440532013000 by Friday", "DE89370400440532013000", SensitiveKind.Id);
        Assert.DoesNotContain(Found("pay to DE89370400440532013001"), f => f.Kind == SensitiveKind.Id);
    }

    // MARK: IP addresses

    [Fact]
    public void IpAddressesAreOffUnlessAskedFor()
    {
        Assert.Empty(Found("server 10.1.2.3", Defaults));
        Masks("server 10.1.2.3.", "10.1.2.3", SensitiveKind.Ip);
        Masks("http://192.168.1.42:8000/v1", "192.168.1.42", SensitiveKind.Ip);
    }

    [Theory]
    [InlineData("listen 127.0.0.1")]
    [InlineData("bind 0.0.0.0")]
    [InlineData("release v1.2.3.4")]
    [InlineData("not 300.1.2.3")]
    [InlineData("1.2.3.4.5")]
    public void SomeDottedQuadsAreNotAddresses(string text) => Assert.DoesNotContain(Found(text), f => f.Kind == SensitiveKind.Ip);

    // MARK: Custom terms, the vault

    [Fact]
    public void CustomTermsMatchWithoutCaseOnWordBoundaries()
    {
        var detector = new SensitiveDetector(SensitiveCategories.Default, ["Acme Corp", "Bob", "C++", "re:PRJ-\\d{4}", "  ", "re:[unclosed"]);
        Assert.Equal([("ACME corp", SensitiveKind.Term)], Found("call ACME corp tomorrow", detector));
        Assert.Equal([("bob", SensitiveKind.Term)], Found("ask bob", detector));
        Assert.Empty(Found("Bobby and bobsled", detector));
        Assert.Equal([("C++", SensitiveKind.Term)], Found("written in C++ mostly", detector));
        Assert.Equal([("prj-1234", SensitiveKind.Term)], Found("ticket prj-1234 is open", detector));
        Assert.Equal(["re:[unclosed"], SensitiveDetector.InvalidTerms(["Acme", "re:[unclosed", "re:ok"]));
        // Off with their category.
        Assert.Empty(Found("ask bob", new SensitiveDetector(SensitiveCategories.Secrets, ["Bob"])));
    }

    [Fact]
    public void VaultSecretsAreMaskedExactlyWhateverTheSwitches()
    {
        var detector = new SensitiveDetector(SensitiveCategories.None, vaultSecrets: ["hunter2-staging", "abc"]);
        Assert.Equal([("hunter2-staging", SensitiveKind.Secret)], Found("the pw is hunter2-staging ok", detector));
        Assert.Empty(Found("the pw is HUNTER2-staging", detector));   // exact match only
        Assert.Empty(Found("abc abc", detector));                     // too short to mask safely
    }

    // MARK: Overlaps

    [Fact]
    public void TheLongestMatchWinsAndPlaceholdersAreLeftAlone()
    {
        // A URL password inside an email-shaped run: the longer email covers it.
        Assert.Equal([("Hunter2xyz@db.example.com", SensitiveKind.Email)], Found("postgres://admin:Hunter2xyz@db.example.com/app", Defaults));
        // Already masked text is never masked again, nor anything inside it.
        Assert.Empty(Found("write to ⟦EMAIL_1⟧ and ⟦TERM_12⟧"));
        Assert.Equal([("a@b.io", SensitiveKind.Email)], Found("⟦EMAIL_1⟧ a@b.io"));
    }

    [Fact]
    public void MatchesComeBackInOrderWithoutOverlapping()
    {
        var text = "mail a@b.io, call +14155550123, key sk-" + new string('k', 24);
        var found = All.Find(text);
        Assert.Equal(3, found.Count);
        for (var i = 1; i < found.Count; i++) Assert.True(found[i].Start >= found[i - 1].End);
    }
}
