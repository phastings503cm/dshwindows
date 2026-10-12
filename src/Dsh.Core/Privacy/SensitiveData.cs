using System.Numerics;
using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - Sensitive data detectors
//
// What the privacy shield masks before a request goes to a model that isn't private: secrets and keys, email
// addresses, phone numbers, ID and card numbers, IP addresses (off unless asked for), the user's own custom terms
// (client names, code names), and every value in the Credentials Vault. The patterns are the same in DSH for
// macOS, with the same test cases, so both apps mask the same things.
//
// (Memory/SecretGuard answers a different question — "may this text be stored?" — and is stricter about it. The
// key formats below are the shield's own list; the Swapper token format was added to SecretGuard too.)

/// <summary>Kinds of sensitive value, each with its own placeholder prefix (⟦EMAIL_1⟧).</summary>
public enum SensitiveKind { Secret, Email, Phone, Id, Card, Ip, Term }

/// <summary>The categories the user switches on and off in Settings › AI Models › Sensitive data.</summary>
[Flags]
public enum SensitiveCategories
{
    None = 0,
    Secrets = 1,
    Emails = 2,
    Phones = 4,
    /// <summary>ID and card numbers: SSNs, IBANs and payment cards.</summary>
    Ids = 8,
    Ips = 16,
    CustomTerms = 32,
    /// <summary>Everything but IP addresses.</summary>
    Default = Secrets | Emails | Phones | Ids | CustomTerms,
    All = Default | Ips,
}

/// <summary>One sensitive value found in a text: where it is and what kind.</summary>
public readonly record struct SensitiveMatch(int Start, int Length, SensitiveKind Kind)
{
    public int End => Start + Length;
}

public static partial class SensitiveKinds
{
    /// <summary>How long one pattern may spend on one search before it gives up (on that text only).</summary>
    internal const int MatchTimeoutMs = 250;

    public static string Prefix(this SensitiveKind kind) => kind switch
    {
        SensitiveKind.Secret => "SECRET",
        SensitiveKind.Email => "EMAIL",
        SensitiveKind.Phone => "PHONE",
        SensitiveKind.Id => "ID",
        SensitiveKind.Card => "CARD",
        SensitiveKind.Ip => "IP",
        _ => "TERM",
    };

    public static SensitiveKind? FromPrefix(string prefix) => prefix switch
    {
        "SECRET" => SensitiveKind.Secret,
        "EMAIL" => SensitiveKind.Email,
        "PHONE" => SensitiveKind.Phone,
        "ID" => SensitiveKind.Id,
        "CARD" => SensitiveKind.Card,
        "IP" => SensitiveKind.Ip,
        "TERM" => SensitiveKind.Term,
        _ => null,
    };

    /// <summary>"⟦EMAIL_1⟧" (U+27E6, U+27E7).</summary>
    public static string Placeholder(SensitiveKind kind, int number) => $"⟦{kind.Prefix()}_{number}⟧";

    /// <summary>A placeholder anywhere in text.</summary>
    [GeneratedRegex(@"⟦(SECRET|EMAIL|PHONE|ID|CARD|IP|TERM)_(\d{1,6})⟧", RegexOptions.CultureInvariant, SensitiveKinds.MatchTimeoutMs)]
    public static partial Regex PlaceholderPattern();

    /// <summary>"key", "email", … — for "Masked 2 emails and 1 key".</summary>
    public static string Noun(this SensitiveKind kind, int count) => (kind, count == 1) switch
    {
        (SensitiveKind.Secret, true) => "key",
        (SensitiveKind.Secret, false) => "keys",
        (SensitiveKind.Email, true) => "email",
        (SensitiveKind.Email, false) => "emails",
        (SensitiveKind.Phone, true) => "phone number",
        (SensitiveKind.Phone, false) => "phone numbers",
        (SensitiveKind.Id, true) => "ID number",
        (SensitiveKind.Id, false) => "ID numbers",
        (SensitiveKind.Card, true) => "card number",
        (SensitiveKind.Card, false) => "card numbers",
        (SensitiveKind.Ip, true) => "IP address",
        (SensitiveKind.Ip, false) => "IP addresses",
        (SensitiveKind.Term, true) => "custom term",
        _ => "custom terms",
    };

    /// <summary>The settings category a kind belongs to.</summary>
    public static SensitiveCategories Category(this SensitiveKind kind) => kind switch
    {
        SensitiveKind.Secret => SensitiveCategories.Secrets,
        SensitiveKind.Email => SensitiveCategories.Emails,
        SensitiveKind.Phone => SensitiveCategories.Phones,
        SensitiveKind.Id or SensitiveKind.Card => SensitiveCategories.Ids,
        SensitiveKind.Ip => SensitiveCategories.Ips,
        _ => SensitiveCategories.CustomTerms,
    };
}

/// <summary>Finds sensitive values in text. Built once per set of settings (the custom terms are compiled then);
/// thread-safe.</summary>
public sealed partial class SensitiveDetector
{
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(250);

    private readonly SensitiveCategories _categories;
    private readonly IReadOnlyList<Regex> _terms;
    /// <summary>Which custom terms' expressions took too long once (and are skipped from then on).</summary>
    private readonly bool[] _termTimedOut;
    private readonly IReadOnlyList<string> _vault;

    /// <param name="customTerms">One term per entry: matched case-insensitively on word boundaries; an entry starting
    /// with "re:" is a regular expression (also case-insensitive). Blank entries and broken expressions are skipped.</param>
    /// <param name="vaultSecrets">Exact values that are always masked, whatever the categories (the Credentials Vault's).</param>
    public SensitiveDetector(SensitiveCategories categories, IEnumerable<string>? customTerms = null, IEnumerable<string>? vaultSecrets = null)
    {
        _categories = categories;
        _terms = categories.HasFlag(SensitiveCategories.CustomTerms) ? CompileTerms(customTerms ?? []) : [];
        _termTimedOut = new bool[_terms.Count];
        _vault = (vaultSecrets ?? []).Where(v => v.Length >= 4).Distinct(StringComparer.Ordinal).OrderByDescending(v => v.Length).ToList();
    }

    /// <summary>The custom-term lines that aren't usable (a "re:" line that isn't a valid expression).</summary>
    public static IReadOnlyList<string> InvalidTerms(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim()).Where(l => l.StartsWith("re:", StringComparison.OrdinalIgnoreCase) && Compile(l) is null).ToList();

    private static IReadOnlyList<Regex> CompileTerms(IEnumerable<string> lines) =>
        lines.Select(l => l.Trim()).Where(l => l.Length > 0).Select(Compile).OfType<Regex>().ToList();

    private static Regex? Compile(string line)
    {
        try
        {
            if (line.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
            {
                var pattern = line[3..].Trim();
                return pattern.Length == 0 ? null : new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);
            }
            // Word boundaries that also work for terms that start or end with a symbol ("C++", "#ops").
            return new Regex(@"(?<!\w)" + Regex.Escape(line) + @"(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    // MARK: Patterns

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex PrivateKeyBegin();

    [GeneratedRegex(@"-----END [A-Z ]*PRIVATE KEY-----", RegexOptions.CultureInvariant, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex PrivateKeyEnd();

    /// <summary>Key formats that identify themselves: the whole match is the secret.</summary>
    private static readonly Regex[] KeyFormats =
    [
        new(@"\b(AKIA|ASIA)[0-9A-Z]{16}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit),          // AWS access key id
        new(@"\bgh[pousr]_[A-Za-z0-9]{36,}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit),       // GitHub
        new(@"\bgithub_pat_[A-Za-z0-9_]{60,}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit),
        new(@"\bsk-(proj-)?[A-Za-z0-9_\-]{20,}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit),   // OpenAI-style
        new(@"\bsk-ant-[A-Za-z0-9_\-]{20,}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit),       // Anthropic
        new(@"\bxox[abprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit),     // Slack
        new(@"\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit),           // Google
        new(@"\b[rs]k_(live|test)_[0-9A-Za-z]{16,}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit), // Stripe
        new(@"\bsst_[0-9a-f]{8}_[A-Za-z0-9_\-]{20,}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit), // Spark Swapper app token
        new(@"\beyJ[A-Za-z0-9_\-]{8,}\.eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\b", RegexOptions.CultureInvariant | RegexOptions.Compiled, Limit), // JWT
    ];

    /// <summary>"Bearer &lt;token&gt;": the token is the secret (16 characters or more).</summary>
    [GeneratedRegex(@"\bBearer\s+([A-Za-z0-9._~+/\-=]{16,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex BearerToken();

    /// <summary>scheme://user:PASSWORD@host: the password is the secret.</summary>
    [GeneratedRegex(@"\b[A-Za-z][A-Za-z0-9+.\-]*://[^\s/:@]+:([^\s/@]+)@", RegexOptions.CultureInvariant, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex UrlPassword();

    /// <summary>password = …, api_key: …, token=…, "password": "…" (JSON, YAML): the value is the secret. A quoted value
    /// always is; a bare one only when it looks like one (<see cref="IsBareSecret"/>) — <c>let token: String</c>,
    /// <c>const token = getToken();</c> and <c>password = user_password</c> are code, not secrets.</summary>
    [GeneratedRegex(@"(?:password|passwd|pwd|secret|api[_-]?key|token|access[_-]?key)[""']?\s*[:=]\s*(?:""(?<dq>[^""\n]{6,})""|'(?<sq>[^'\n]{6,})'|(?<bare>[^\s""'`;,(){}\[\]<>]{6,}))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex Assignment();

    /// <summary>A line of an env file or a shell export: <c>DB_PASSWORD=…</c>, <c>export GITHUB_TOKEN=…</c>. The value is
    /// always the secret.</summary>
    [GeneratedRegex(@"^[ \t]*(?:export[ \t]+)?[A-Z0-9_]*(?:PASSWORD|PASSWD|SECRET|TOKEN|API_KEY|ACCESS_KEY)[A-Z0-9_]*[ \t]*=[ \t]*(?<value>\S+)",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex EnvLine();

    /// <summary>Names of types, and other words a declaration puts where a value would be.</summary>
    private static readonly HashSet<string> TypeWords = new(StringComparer.Ordinal)
    {
        "String", "string", "str", "Int", "int", "Integer", "Bool", "bool", "boolean", "Boolean", "number", "Number", "any", "Any",
        "Data", "Date", "URL", "Object", "object", "None", "null", "nil", "true", "false", "undefined", "Optional", "SecureString",
        "char", "bytes", "Buffer", "Uint8Array", "Secret", "Token", "Password", "Credential", "Credentials",
    };

    [GeneratedRegex(@"^[A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)+$", RegexOptions.CultureInvariant, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex DottedPath();

    [GeneratedRegex(@"^[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]*)+$", RegexOptions.CultureInvariant, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex PascalCase();

    [GeneratedRegex(@"^[a-z]+(?:[A-Z][a-z0-9]*)+$", RegexOptions.CultureInvariant, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex CamelCase();

    [GeneratedRegex(@"^[a-z]+(?:_[a-z0-9]+)+$", RegexOptions.CultureInvariant, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex SnakeCase();

    /// <summary>Whether a bare (unquoted) assigned value is a secret rather than code: not a type, a dotted path or an
    /// identifier, and either with a digit or a symbol in it, or a run of eight or more letters all in one case.
    /// (Its trailing ?, ! or . is left out first: <c>String?</c>, the end of a sentence.)</summary>
    internal static bool IsBareSecret(string value)
    {
        var v = value.TrimEnd('?', '!', '.');
        if (v.Length == 0 || TypeWords.Contains(v)) return false;
        if (DottedPath().IsMatch(v) || PascalCase().IsMatch(v) || CamelCase().IsMatch(v) || SnakeCase().IsMatch(v)) return false;
        if (v.Any(char.IsAsciiDigit)) return true;
        if (v.Any(c => !char.IsAsciiLetter(c) && c != '_')) return true;
        return v.Length >= 8 && (v.All(char.IsAsciiLetterLower) || v.All(char.IsAsciiLetterUpper));
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex Email();

    [GeneratedRegex(@"\+[1-9]\d{7,14}", RegexOptions.CultureInvariant, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex E164();

    /// <summary>North American numbers written with separators: (555) 123-4567, 555.123.4567, 555-123-4567.</summary>
    [GeneratedRegex(@"\(?\d{3}\)?[\s.\-]\d{3}[\s.\-]\d{4}", RegexOptions.CultureInvariant, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex Nanp();

    [GeneratedRegex(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.CultureInvariant, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex Ssn();

    /// <summary>13–19 digits, optionally in groups split by single spaces or dashes; kept only when Luhn agrees.</summary>
    [GeneratedRegex(@"\b\d(?:[ \-]?\d){12,30}\b", RegexOptions.CultureInvariant, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex CardCandidate();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}[A-Z0-9]{11,30}\b", RegexOptions.CultureInvariant, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex Iban();

    [GeneratedRegex(@"(?<![\d.vV])(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})(?!\.?\d)", RegexOptions.CultureInvariant, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex Ipv4();

    // MARK: Finding

    /// <summary>Every sensitive value in <paramref name="text"/>, in order, never overlapping (where detectors overlap
    /// the longest match wins) and never inside a placeholder that is already there.</summary>
    public IReadOnlyList<SensitiveMatch> Find(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var found = new List<SensitiveMatch>();
        void Add(int start, int length, SensitiveKind kind)
        {
            if (length > 0) found.Add(new SensitiveMatch(start, length, kind));
        }

        foreach (var value in _vault)
        {
            for (var at = text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = text.IndexOf(value, at + value.Length, StringComparison.Ordinal))
                Add(at, value.Length, SensitiveKind.Secret);
        }
        if (_categories.HasFlag(SensitiveCategories.Secrets)) FindSecrets(text, Add);
        if (_categories.HasFlag(SensitiveCategories.Emails))
            Each(Email(), text, m => Add(m.Index, m.Length, SensitiveKind.Email));
        if (_categories.HasFlag(SensitiveCategories.Phones))
        {
            Each(E164(), text, m => Add(m.Index, m.Length, SensitiveKind.Phone));
            Each(Nanp(), text, m => Add(m.Index, m.Length, SensitiveKind.Phone));
        }
        if (_categories.HasFlag(SensitiveCategories.Ids)) FindIds(text, Add);
        if (_categories.HasFlag(SensitiveCategories.Ips))
        {
            Each(Ipv4(), text, m =>
            {
                if (!Enumerable.Range(1, 4).All(g => int.Parse(m.Groups[g].ValueSpan, System.Globalization.CultureInfo.InvariantCulture) <= 255)) return;
                if (m.Value is "127.0.0.1" or "0.0.0.0") return;
                Add(m.Index, m.Length, SensitiveKind.Ip);
            });
        }
        for (var i = 0; i < _terms.Count; i++)
        {
            // A term's own expression that once took too long is set aside: it would cost the same on every text.
            if (Volatile.Read(ref _termTimedOut[i])) continue;
            if (!Each(_terms[i], text, m => Add(m.Index, m.Length, SensitiveKind.Term))) Volatile.Write(ref _termTimedOut[i], true);
        }
        return Resolve(found, Placeholders(text));
    }

    /// <summary>Every match of <paramref name="pattern"/> in <paramref name="text"/>, handed to <paramref name="use"/>. A
    /// pattern that takes too long on this text gives up (false), keeping what it had found; the others still run.</summary>
    private static bool Each(Regex pattern, string text, Action<Match> use)
    {
        try
        {
            for (var m = pattern.Match(text); m.Success; m = m.NextMatch()) use(m);
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static void FindSecrets(string text, Action<int, int, SensitiveKind> add)
    {
        Each(PrivateKeyBegin(), text, begin => PrivateKey(text, begin, add));
        foreach (var format in KeyFormats)
            Each(format, text, m => add(m.Index, m.Length, SensitiveKind.Secret));
        Each(BearerToken(), text, m => add(m.Groups[1].Index, m.Groups[1].Length, SensitiveKind.Secret));
        Each(UrlPassword(), text, m =>
        {
            var password = m.Groups[1];
            if (!IsPlaceholderValue(password.Value)) add(password.Index, password.Length, SensitiveKind.Secret);
        });
        Each(Assignment(), text, m =>
        {
            Group value;
            if (m.Groups["dq"] is { Success: true } dq) value = dq;
            else if (m.Groups["sq"] is { Success: true } sq) value = sq;
            else
            {
                value = m.Groups["bare"];
                if (!IsBareSecret(value.Value)) return;
                // The end of a sentence isn't part of the secret ("my password: hunter22x.").
                var bare = value.Value.TrimEnd('?', '!', '.');
                if (!IsPlaceholderValue(bare)) add(value.Index, bare.Length, SensitiveKind.Secret);
                return;
            }
            if (!IsPlaceholderValue(value.Value)) add(value.Index, value.Length, SensitiveKind.Secret);
        });
        Each(EnvLine(), text, m =>
        {
            var value = m.Groups["value"];
            var (start, length) = (value.Index, value.Length);
            // A quoted value is the text between the quotes.
            if (length >= 2 && text[start] is '"' or '\'' && text[start + length - 1] == text[start])
            {
                start++;
                length -= 2;
            }
            if (length > 0 && !IsPlaceholderValue(text.Substring(start, length))) add(start, length, SensitiveKind.Secret);
        });
    }

    /// <summary>A private key block from its BEGIN line through its END line (or, cut off, the end of its body).</summary>
    private static void PrivateKey(string text, Match begin, Action<int, int, SensitiveKind> add)
    {
        int stop;
        Match end;
        try
        {
            end = PrivateKeyEnd().Match(text, begin.Index + begin.Length);
        }
        catch (RegexMatchTimeoutException)
        {
            end = Match.Empty;
        }
        if (end.Success) stop = end.Index + end.Length;
        else
        {
            // A block cut off before its END line: mask through the end of its body.
            stop = begin.Index + begin.Length;
            while (stop < text.Length && (char.IsAsciiLetterOrDigit(text[stop]) || text[stop] is '+' or '/' or '=' or '\r' or '\n' or ' ' or '\t'))
                stop++;
        }
        add(begin.Index, stop - begin.Index, SensitiveKind.Secret);
    }

    private static void FindIds(string text, Action<int, int, SensitiveKind> add)
    {
        Each(Ssn(), text, m => add(m.Index, m.Length, SensitiveKind.Id));
        Each(Iban(), text, m =>
        {
            if (IbanValid(m.Value)) add(m.Index, m.Length, SensitiveKind.Id);
        });
        Each(CardCandidate(), text, m =>
        {
            if (CardWithin(m.Value) is var (offset, length)) add(m.Index + offset, length, SensitiveKind.Card);
        });
    }

    /// <summary>A stand-in rather than a real value: ***, &lt;your key&gt;, ${TOKEN}, xxxxxx, changeme, your-api-key — or a
    /// placeholder the app already put there (the shield's ⟦…⟧, the Credentials Vault's {{vault:NAME}} and [vault:NAME]).</summary>
    public static bool IsPlaceholderValue(string value)
    {
        var v = value.Trim().Trim('"', '\'', ',', ';');
        if (v.Length == 0) return true;
        if (v.All(c => c == '*')) return true;
        if (v.StartsWith('<') || v.StartsWith("${", StringComparison.Ordinal) || v.StartsWith("{{", StringComparison.Ordinal)) return true;
        if (v.StartsWith("[vault:", StringComparison.Ordinal)) return true;
        if (v.StartsWith("xxx", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.Equals("changeme", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.StartsWith("your-", StringComparison.OrdinalIgnoreCase) || v.StartsWith("your_", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.Contains('⟦')) return true;
        // A reference to where the value lives, not the value: $DB_PASSWORD, $env:API_KEY, %TOKEN%.
        if (VariableReference().IsMatch(v)) return true;
        return false;
    }

    [GeneratedRegex(@"^(?:\$(?:env:)?[A-Za-z_][A-Za-z0-9_]*|%[A-Za-z_][A-Za-z0-9_]*%)$", RegexOptions.CultureInvariant, SensitiveKinds.MatchTimeoutMs)]
    private static partial Regex VariableReference();

    /// <summary>The card number inside a run of digits (14–19 of them, starting like a card network's numbers and
    /// passing Luhn): the whole run, or a run of whole groups from its start or its end (a card written next to other
    /// digits). Null when there's none.</summary>
    internal static (int Offset, int Length)? CardWithin(string run)
    {
        static string Digits(string s) => new(s.Where(char.IsAsciiDigit).ToArray());
        static bool IsCard(string d) => d.Length is >= 14 and <= 19 && HasCardPrefix(d) && d.Distinct().Count() > 1 && Luhn(d);
        var all = Digits(run);
        if (IsCard(all)) return (0, run.Length);
        // Groups split by the separators: try the longest leading and trailing runs of whole groups.
        var groups = new List<(int Start, int Length)>();
        var start = 0;
        for (var i = 0; i <= run.Length; i++)
        {
            if (i < run.Length && char.IsAsciiDigit(run[i])) continue;
            if (i > start) groups.Add((start, i - start));
            start = i + 1;
        }
        if (groups.Count < 2) return null;
        for (var take = groups.Count - 1; take >= 1; take--)
        {
            foreach (var from in new[] { 0, groups.Count - take })
            {
                var first = groups[from];
                var last = groups[from + take - 1];
                var piece = run.Substring(first.Start, last.Start + last.Length - first.Start);
                var digits = Digits(piece);
                if (IsCard(digits)) return (first.Start, piece.Length);
            }
        }
        return null;
    }

    /// <summary>Whether digits start like a card people carry (Visa, Mastercard, Amex, Discover, JCB, Diners,
    /// UnionPay). A tenth of any digit run passes Luhn by chance; this keeps millisecond timestamps (17…) and other
    /// long numbers in code from being taken for cards.</summary>
    internal static bool HasCardPrefix(string d)
    {
        int Prefix(int n) => d.Length >= n ? int.Parse(d[..n], System.Globalization.CultureInfo.InvariantCulture) : -1;
        return d[0] switch
        {
            '4' => true,
            '5' => Prefix(2) is >= 51 and <= 55,
            '2' => Prefix(4) is >= 2221 and <= 2720,
            '3' => Prefix(2) is 34 or 37 or 35 or 36 or 38 or 39 || Prefix(3) is >= 300 and <= 305,
            '6' => Prefix(4) == 6011 || Prefix(2) is 65 or 62 || Prefix(3) is >= 644 and <= 649,
            _ => false,
        };
    }

    /// <summary>The Luhn checksum card numbers carry.</summary>
    public static bool Luhn(string digits)
    {
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (d is < 0 or > 9) return false;
            if (doubleIt)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
            doubleIt = !doubleIt;
        }
        return digits.Length > 0 && sum % 10 == 0;
    }

    /// <summary>An IBAN whose mod-97 check (ISO 13616) holds.</summary>
    public static bool IbanValid(string iban)
    {
        if (iban.Length < 15 || iban.Length > 34) return false;
        var rearranged = iban[4..] + iban[..4];
        var number = new System.Text.StringBuilder();
        foreach (var c in rearranged)
        {
            if (char.IsAsciiDigit(c)) number.Append(c);
            else if (c is >= 'A' and <= 'Z') number.Append(c - 'A' + 10);
            else return false;
        }
        return BigInteger.Parse(number.ToString(), System.Globalization.CultureInfo.InvariantCulture) % 97 == 1;
    }

    /// <summary>Where the placeholders already in the text are (nothing may be matched inside them), in order.</summary>
    internal static List<(int Start, int End)> Placeholders(string text)
    {
        if (!text.Contains('⟦')) return [];
        var found = new List<(int Start, int End)>();
        Each(SensitiveKinds.PlaceholderPattern(), text, m => found.Add((m.Index, m.Index + m.Length)));
        return found;
    }

    /// <summary>The matches to keep, in order: where matches overlap the longest wins (then the earliest, then the first
    /// found), and a match that touches a placeholder already there is dropped. One sort and one sweep: overlapping
    /// matches form small clusters, and only within a cluster are lengths compared.</summary>
    internal static IReadOnlyList<SensitiveMatch> Resolve(List<SensitiveMatch> found, List<(int Start, int End)> placeholders)
    {
        if (found.Count == 0) return [];
        var order = new int[found.Count];
        for (var i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            var x = found[a];
            var y = found[b];
            if (x.Start != y.Start) return x.Start.CompareTo(y.Start);
            if (x.Length != y.Length) return y.Length.CompareTo(x.Length);
            return a.CompareTo(b);
        });
        var kept = new List<SensitiveMatch>(found.Count);
        var cluster = new List<int>();
        var clusterEnd = int.MinValue;
        var next = 0; // the first placeholder that could still overlap
        foreach (var index in order)
        {
            var match = found[index];
            while (next < placeholders.Count && placeholders[next].End <= match.Start) next++;
            if (next < placeholders.Count && placeholders[next].Start < match.End) continue;
            if (cluster.Count > 0 && match.Start >= clusterEnd) Settle(found, cluster, kept);
            cluster.Add(index);
            clusterEnd = cluster.Count == 1 ? match.End : Math.Max(clusterEnd, match.End);
        }
        Settle(found, cluster, kept);
        return kept;
    }

    /// <summary>One cluster of overlapping matches: longest first, each kept unless it overlaps one kept before it.</summary>
    private static void Settle(List<SensitiveMatch> found, List<int> cluster, List<SensitiveMatch> kept)
    {
        if (cluster.Count == 1)
        {
            kept.Add(found[cluster[0]]);
        }
        else if (cluster.Count > 1)
        {
            var taken = new List<SensitiveMatch>();
            foreach (var index in cluster.OrderByDescending(i => found[i].Length).ThenBy(i => found[i].Start).ThenBy(i => i))
            {
                var match = found[index];
                if (taken.Any(t => match.Start < t.End && t.Start < match.End)) continue;
                taken.Add(match);
            }
            kept.AddRange(taken.OrderBy(m => m.Start));
        }
        cluster.Clear();
    }
}
