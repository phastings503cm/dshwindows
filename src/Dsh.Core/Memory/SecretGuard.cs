using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - Guarding against secrets
//
// Memory is plain text on disk and is quoted back into prompts, and a skill or a note travels to the model provider with
// every request, so text that holds a credential must never be stored or imported. Four things are recognised:
//  * strings that identify themselves — an AWS key id, a GitHub token, a PEM key, a JWT, a webhook URL;
//  * credentials in a URL — scheme://user:password@host;
//  * a value given to a name that says it is secret — DB_PASSWORD=…, "clientSecret": "…", "the password is …", `--password …`;
//  * the same in the shapes config files and code give it — a name and a `value` next to each other (Kubernetes, ECS, NuGet),
//    an XML element, `os.environ["DB_PASSWORD"] = "…"`, `curl -u user:password`, `docker login -p …`.
// The third is where care is needed: a note that says "the password is required" or "Token limit: 128000", a script that
// reads os.environ["API_KEY"], or a README with API_KEY=${API_KEY} is not a leak, and neither is a pointer to where a secret
// lives ("Secret name: prod-db-creds", "Encryption key ID: alias/prod-app"). So a value only counts when it looks like
// a literal (not a reference, a placeholder, a plain word, a sentence, a date or a version), and the scan is written by hand so
// that no text can make it more than linear: every step looks at a bounded stretch of text. (Pathological text costs a few
// microseconds a character; text that defeats a pattern outright is refused rather than let through.)

/// <summary>Recognises text that looks like a credential. Credentials go in the vault.</summary>
public static class SecretGuard
{
    /// <summary>Changes whenever the rules do, so notes indexed under older rules are screened again.</summary>
    public const int Revision = 4;

    /// <summary>A pattern that takes longer than this on some pathological text refuses the text.</summary>
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(200);

    private static Regex Format(string pattern, RegexOptions extra = RegexOptions.None) =>
        new(pattern, RegexOptions.CultureInvariant | extra, Limit);

    private static readonly Regex[] Formats =
    [
        Format(@"\bsk-[A-Za-z0-9_\-]{20,}"),                                     // OpenAI-style, Anthropic, OpenRouter
        Format(@"\bsk_(?:live|test)_[A-Za-z0-9]{16,}"),                          // Stripe
        Format(@"\brk_live_[A-Za-z0-9]{16,}"),                                   // Stripe, restricted
        Format(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b"),                                // AWS access key id
        Format(@"\bgh[pousr]_[A-Za-z0-9]{30,}"),                                 // GitHub
        Format(@"\bgithub_pat_[A-Za-z0-9_]{30,}"),
        Format(@"\bglpat-[A-Za-z0-9_\-]{20,}"),                                  // GitLab
        Format(@"\bnpm_[A-Za-z0-9]{36}\b"),                                      // npm
        Format(@"\bxox[abprs]-[A-Za-z0-9\-]{10,}"),                              // Slack
        Format(@"\bxapp-\d-[A-Za-z0-9]{6,}-\d+-[A-Za-z0-9]{20,}"),
        Format(@"\bhooks\.slack\.com/services/[A-Za-z0-9]{6,}/[A-Za-z0-9]{6,}/[A-Za-z0-9]{16,}"), // a Slack webhook is a credential
        Format(@"\bdiscord(?:app)?\.com/api/webhooks/\d{10,}/[A-Za-z0-9_\-]{30,}"),
        Format(@"\bAIza[0-9A-Za-z_\-]{30,}"),                                    // Google
        Format(@"\bya29\.[A-Za-z0-9_\-]{30,}"),                                  // Google OAuth
        Format(@"\bhf_[A-Za-z0-9]{30,}"),                                        // Hugging Face
        Format(@"\bnvapi-[A-Za-z0-9_\-]{40,}"),                                  // NVIDIA
        Format(@"\bgsk_[A-Za-z0-9]{40,}"),                                       // Groq
        Format(@"\bpplx-[A-Za-z0-9]{40,}"),                                      // Perplexity
        Format(@"\bxai-[A-Za-z0-9]{40,}"),                                       // xAI
        Format(@"\bdop_v1_[a-f0-9]{40,}"),                                       // DigitalOcean
        Format(@"\bSG\.[A-Za-z0-9_\-]{16,}\.[A-Za-z0-9_\-]{30,}"),               // SendGrid
        Format(@"\bABSK[A-Za-z0-9+/=_\-]{20,}"),                                 // Amazon Bedrock API key (long-term)
        Format(@"\bbedrock-api-key-[A-Za-z0-9+/=_\-]{20,}"),                     // Amazon Bedrock API key (short-term)
        Format(@"\bAccountKey=[A-Za-z0-9+/]{40,}={0,2}"),                        // Azure storage
        Format(@"-----BEGIN [A-Z ]*PRIVATE KEY(?: BLOCK)?-----[ \t]*\r?\n[ \t]*[A-Za-z0-9+/]{4,}"), // a key: its body starts on the next line (not the sentence that names the header)
        Format(@"\bLS0tLS1CRUdJTi[A-Za-z0-9+/=]{20,}"),                          // a PEM file, base64 of "-----BEGIN"
        Format(@"\beyJ[A-Za-z0-9_\-]{15,}\.[A-Za-z0-9_\-]{15,}\.[A-Za-z0-9_\-]{10,}"), // JWT
        Format(@"\bbearer\s+[A-Za-z0-9._~+/\-]{20,}", RegexOptions.IgnoreCase),
        Format(@"\bauthorization\s*:\s*(?:token|api-?key|key|bearer)\s+[A-Za-z0-9._~+/\-=]{12,}", RegexOptions.IgnoreCase),
        Format(@"\bauthorization\s*:\s*basic\s+[A-Za-z0-9+/=]{16,}", RegexOptions.IgnoreCase),
        Format(@"""auth""\s*:\s*""[A-Za-z0-9+/=]{20,}"""),                       // a Docker config.json login
    ];

    /// <summary>What documentation puts where a key goes: AKIAIOSFODNN7EXAMPLE, sk-xxxxxxxx, xoxb-your-token-here.</summary>
    private static readonly Regex DocumentationKey = new(@"example|x{6,}|(?<![A-Za-z])your(?![A-Za-z])|placeholder|redacted",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    /// <summary>How much text one pass reads. A pattern walking a whole megabyte inside its time limit is at the mercy of the
    /// machine (a busy or slow one turns a benign document into a refusal), so a long text is read in windows.</summary>
    private const int WindowLength = 32_768;

    /// <summary>How much two neighbouring windows share: longer than anything the scan links together (a name, its value and
    /// what lies between are a few hundred characters), so whatever it would find in the whole text lies inside one window.</summary>
    private const int WindowOverlap = 4_096;

    public static bool LooksLikeSecret(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Length <= WindowLength) return Scan(text);
        for (var start = 0; ; start += WindowLength - WindowOverlap)
        {
            var length = Math.Min(WindowLength, text.Length - start);
            if (Scan(text.Substring(start, length))) return true;
            if (start + length >= text.Length) return false;
        }
    }

    private static bool Scan(string text) =>
        HasKeyFormat(text) || HasCredentialInUrl(text) || HasNamedSecret(text) || HasStructuredSecret(text);

    private static bool HasKeyFormat(string text)
    {
        foreach (var pattern in Formats)
        {
            try
            {
                foreach (Match match in pattern.Matches(text))
                {
                    if (!DocumentationKey.IsMatch(match.Value)) return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return true; // text that defeats a pattern is not text to keep
            }
        }
        return false;
    }

    // MARK: URLs

    /// <summary>scheme://user:password@host — the password being something other than a stand-in. The password may hold an
    /// '@' or a '#' (<c>mysql://root:P@ssw0rd@host</c>): the address begins after the last '@' before the path.</summary>
    private static bool HasCredentialInUrl(string text)
    {
        var from = 0;
        while (from < text.Length)
        {
            var scheme = text.IndexOf("://", from, StringComparison.Ordinal);
            if (scheme < 0) return false;
            from = scheme + 3;
            var end = from;
            while (end < text.Length && end - from < 300 && text[end] is not ('/' or '?') && !char.IsWhiteSpace(text[end])) end++;
            if (end == from) continue;
            var at = text.LastIndexOf('@', end - 1, end - from);
            if (at < from) continue;
            var userinfo = text[from..at];
            var colon = userinfo.IndexOf(':');
            if (colon < 0) continue;
            var user = userinfo[..colon];
            var password = userinfo[(colon + 1)..];
            if (password.Length >= 3 && !IsReferenceOrPlaceholder(password) && !IsStandInPassword(password, user)) return true;
        }
        return false;
    }

    /// <summary>The passwords of documentation and of default installs: postgres:postgres@, guest:guest@, user:password@, scott:tiger@.</summary>
    private static bool IsStandInPassword(string password, string user) =>
        password.Equals(user, StringComparison.OrdinalIgnoreCase)
        || password.ToLowerInvariant() is "password" or "pass" or "passwd" or "pwd" or "secret" or "changeme" or "yourpassword" or "mypassword"
            or "xxxx" or "user" or "example" or "postgres" or "root" or "admin" or "guest" or "redis" or "mysql" or "mongo" or "anonymous"
            or "test" or "demo" or "default" or "tiger";

    // MARK: Names that say "secret"

    /// <summary>What makes a name a secret one. (Not "key" alone.) "pw" and "pass" are too common in other senses to count without a
    /// long, literal-looking value — and they end a name (DB_PASS, dbPass) as well as stand alone. A few words for "password" in
    /// other languages.</summary>
    private static readonly Regex Keyword = new(
        @"passphrase|passcode|password|passwd|pwd|secret|token|api[_\-. ]?key|private[_\-. ]?key|(?:encryption|signing|master|app|access)[_\-. ]?key"
        + @"|identified[ \t\u00A0]{1,8}by|(?<![A-Za-z0-9])(?:pw|pass)(?![A-Za-z0-9])|(?-i:(?<=[a-z0-9])(?:Pw|Pass)(?![a-z0-9]))"
        + @"|密码|口令|密钥|パスワード|비밀번호|пароль|passwort|kennwort|contraseña|mot de passe",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    /// <summary>A name ending in _KEY (STRIPE_KEY, HMAC_KEY): a secret only when its value looks random, and not when it is a
    /// key of another kind (a public key, a primary key, a cache key).</summary>
    private static readonly Regex KeyCompound = new(@"(?<![A-Za-z0-9])([A-Za-z0-9]{1,24})[_\-]key(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    private static readonly HashSet<string> OtherKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        "public", "primary", "foreign", "sort", "partition", "cache", "hash", "range", "row", "shard", "search", "lookup", "index", "map",
        "object", "s3", "dictionary", "translation", "config", "feature", "flag", "ssh", "gpg", "pgp", "license", "i18n",
    };

    /// <summary>Words that may follow the keyword inside one name: SECRET_ACCESS_KEY, token_prod, apiKeyValue, SECRET_KEY_BASE64.</summary>
    private static readonly string[] SuffixWords =
    [
        "production", "development", "password", "primary", "private", "staging", "backup", "client", "access", "secret", "base64",
        "token", "admin", "stage", "value", "user", "root", "live", "test", "auth", "prod", "pass", "base", "new", "old", "pwd", "api",
        "key", "dev", "id", "db",
    ];

    /// <summary>How many suffix words one name can have (SECRET_ACCESS_KEY_ID_PROD…): a longer chain is not a name, and walking
    /// it for every keyword in a text made of nothing else would be slow.</summary>
    private const int MaxSuffixWords = 6;

    /// <summary>Words that say what a secret is ABOUT rather than what it is: "Secret name: prod-db-creds", "Encryption key ID:
    /// alias/prod-app", "Access token TTL: 24h", "Password last changed: 2024-01-15". What follows is a pointer, not a credential.</summary>
    private static readonly HashSet<string> Descriptors = new(StringComparer.Ordinal)
    {
        "name", "names", "id", "ids", "arn", "path", "paths", "file", "files", "filename", "location", "store", "stores", "manager", "vault",
        "policy", "policies", "type", "types", "version", "versions", "alias", "aliases", "ttl", "lifetime", "expiry", "expires", "expiration",
        "expire", "age", "length", "format", "limit", "limits", "count", "size", "timeout", "endpoint", "url", "uri", "header", "headers",
        "field", "fields", "label", "description", "owner", "rotation", "rotated", "last", "changed", "updated", "created", "date", "status",
        "reset", "generation", "algorithm", "provider", "source", "scope", "hash", "hashed", "hashing", "requirement", "requirements", "rule",
        "rules", "prefix", "suffix", "storage", "service",
    };

    /// <summary>"is", "was": what links a name to its value in a sentence.</summary>
    private static readonly HashSet<string> LinkWords = new(StringComparer.Ordinal) { "is", "was", "are", "equals", "be", "becomes" };

    /// <summary>What may come between the link word and the value: "is now X", "was changed to X", "is currently set to X".</summary>
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "now", "currently", "still", "set", "changed", "reset", "updated", "rotated", "to", "as", "at", "into",
    };

    /// <summary>The words before a "token" that make it something else than a credential: a pagination or idempotency token
    /// (NextToken, ClientRequestToken), a .NET public key token (PublicKeyToken=b77a5c561934e089), a CancellationToken.</summary>
    private const string NotCredentialWords = @"(?:next|continuation|page|paging|client_?request|request|idempotency|sync|cursor|resume|lock|starting|marker|csrf|xsrf|public_?key|cancellation)";

    private static readonly Regex NonCredentialToken = new(NotCredentialWords + @"[_\-.]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    private enum Strength { Weak, Medium, Strong }

    /// <summary>One value a name may have been given, and how sure the link is: a <c>Strict</c> one (the name was only guessed
    /// at, or the value merely follows it) must look random to count.</summary>
    private readonly record struct Candidate(string Value, bool Strict);

    private static bool HasNamedSecret(string text)
    {
        var candidates = new List<Candidate>(4);
        try
        {
            foreach (Match match in Keyword.Matches(text))
            {
                var strength = StrengthOf(match.Value);
                candidates.Clear();
                Collect(text, match.Index, match.Length, strength, forceStrict: false, candidates);
                foreach (var found in candidates)
                {
                    if (LooksLikeLiteral(found.Value, strength, found.Strict)) return true;
                }
            }
            foreach (Match match in KeyCompound.Matches(text))
            {
                if (OtherKinds.Contains(match.Groups[1].Value)) continue;
                candidates.Clear();
                Collect(text, match.Index, match.Length, Strength.Medium, forceStrict: true, candidates);
                foreach (var found in candidates)
                {
                    if (LooksLikeLiteral(found.Value, Strength.Medium, strict: true)) return true;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return true; // text that defeats a pattern is not text to keep
        }
        return false;
    }

    private static Strength StrengthOf(string keyword)
    {
        var lower = keyword.ToLowerInvariant();
        if (lower is "pw" or "pass") return Strength.Weak;
        return lower.StartsWith("pass", StringComparison.Ordinal) || lower is "pwd" or "secret" || lower.StartsWith("private", StringComparison.Ordinal)
               || (lower.Length > 0 && lower[0] > 127) || lower.StartsWith("kennwort", StringComparison.Ordinal)
               || lower.StartsWith("contrase", StringComparison.Ordinal) || lower.StartsWith("mot ", StringComparison.Ordinal)
            ? Strength.Strong
            : Strength.Medium;
    }

    private static bool IsSeparator(char c) => c is ':' or '=' or '：' or '＝';

    /// <summary>The values the text gives to the name that ends <paramref name="length"/> characters after <paramref name="index"/>:
    /// <c>name=value</c>, <c>"name": "value"</c>, <c>**Name:** value</c>, <c>| Name | value |</c>, <c>the password for x is value</c>,
    /// <c>--password value</c>, <c>PASSWORD 'value'</c>, <c>staging pw value</c>.</summary>
    private static void Collect(string text, int index, int length, Strength strength, bool forceStrict, List<Candidate> into)
    {
        if (NonCredentialToken.IsMatchSafe(NameBefore(text, index))) return;
        var nameOk = AtNameStart(text, index);
        var at = SkipNameSuffix(text, index + length);
        var afterName = at;
        var closers = SkipClosers(text, at, out var sawPipe);
        if (sawPipe && !LineStartsWithPipe(text, index)) sawPipe = false; // (a `||` in code is not a table)
        at = closers;

        // name = value — also when the keyword ends a lowercase compound (authtoken:, dbpassword=).
        if (at < text.Length && IsSeparator(text[at]) && (nameOk || strength != Strength.Weak))
        {
            at++;
            if (at < text.Length && text[at] is '=' or '>') at++; // ":=", "=>", "=="
            Add(into, ReadValueAfterSeparator(text, SkipLeaders(text, at)), forceStrict);
            return;
        }

        // name_UNLISTED = value: a name with a word we do not know in it (AWS_BEARER_TOKEN_BEDROCK, API_KEY_OPENROUTER); the value has to look random.
        if ((nameOk || strength != Strength.Weak) && GenericTail(text, afterName) is { } tail)
        {
            at = SkipClosers(text, tail.End, out _);
            if (!tail.Descriptive && at < text.Length && IsSeparator(text[at]))
            {
                at++;
                if (at < text.Length && text[at] is '=' or '>') at++;
                Add(into, ReadValueAfterSeparator(text, SkipLeaders(text, at)), strict: true);
            }
            return;
        }
        if (!nameOk) return;

        // | Name | value |
        if (sawPipe && closers < text.Length && text[closers] is not ('\n' or '\r'))
        {
            Add(into, ReadValue(text, closers, untilPipe: true), forceStrict);
            return;
        }

        // --password value, -Password "value", PASSWORD 'value', WITH PASSWORD 'value'
        if (closers > afterName && closers < text.Length)
        {
            var flag = index > 0 && text[index - 1] == '-';
            var quoted = text[closers] is '\'' or '"';
            if (flag || (quoted && strength != Strength.Weak))
            {
                Add(into, ReadValue(text, closers), forceStrict);
                return;
            }
        }

        // The prose form: "the password is X", "the staging database password for the app user is X", "admin password on the router: X".
        // (Not for "pass" and "pw": "you can pass the kwargs is_xml=True" is not a password.)
        var probe = afterName;
        if (strength != Strength.Weak && probe < text.Length && IsBlank(text[probe]))
        {
            for (var words = 0; words < 6; words++)
            {
                probe = SkipBlanks(text, probe);
                var wordLength = WordLength(text, probe);
                if (wordLength == 0) break;
                var end = probe + wordLength;
                if (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) break; // ("is_xml" is not "is")
                var word = text.Substring(probe, wordLength).ToLowerInvariant();
                if (Descriptors.Contains(word)) return; // "Secret name: …", "Password last changed: …": about the secret, not the secret
                probe = SkipBlanks(text, end);
                if (LinkWords.Contains(word))
                {
                    if (probe < text.Length && IsSeparator(text[probe])) probe++;
                    Add(into, ReadValue(text, SkipLeaders(text, SkipFillers(text, probe))), forceStrict);
                    return;
                }
                if (probe < text.Length && IsSeparator(text[probe]))
                {
                    probe++;
                    Add(into, ReadValue(text, SkipLeaders(text, probe)), forceStrict);
                    return;
                }
            }
        }

        // The value simply follows the name: "staging pw Xk29mQ788abZ", ".netrc … password Xk29mQ788abZ", "set the password to Xk29mQ788abZ".
        var next = afterName;
        if (next < text.Length && IsBlank(text[next]))
        {
            next = SkipBlanks(text, next);
            var wordLength = WordLength(text, next);
            if (wordLength > 0 && text.Substring(next, wordLength).ToLowerInvariant() is "to" or "is" or "as")
            {
                next = SkipBlanks(text, next + wordLength);
            }
            Add(into, ReadValue(text, next), strict: true);
        }
    }

    private static void Add(List<Candidate> into, string? found, bool strict)
    {
        if (found is not null) into.Add(new Candidate(found, strict));
    }

    /// <summary>The keyword begins its name: after a break, or on a camelCase hump (clientSecret) or inside an upper-case run (PGPASSWORD).</summary>
    private static bool AtNameStart(string text, int index) =>
        index == 0 || !char.IsLetterOrDigit(text[index - 1]) || char.IsUpper(text[index]);

    /// <summary>The (glued) name before a keyword, at most 24 characters of it: "Next" in NextToken.</summary>
    private static string NameBefore(string text, int index)
    {
        var start = index;
        while (start > 0 && index - start < 24 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] is '_' or '-' or '.')) start--;
        return text[start..index];
    }

    /// <summary>A space, a tab or a no-break space (a pasted note often has one) — not a line break.</summary>
    private static bool IsBlank(char c) => char.IsWhiteSpace(c) && c is not ('\n' or '\r');

    private static int SkipBlanks(string text, int at)
    {
        while (at < text.Length && IsBlank(text[at])) at++;
        return at;
    }

    /// <summary>Whether the line the index is on begins with a table bar.</summary>
    private static bool LineStartsWithPipe(string text, int index)
    {
        var start = index;
        while (start > 0 && index - start < 400 && text[start - 1] is not '\n') start--;
        while (start < index && IsBlank(text[start])) start++;
        return start < text.Length && text[start] == '|';
    }

    /// <summary>What may sit between a name and its separator: a closing quote or Markdown emphasis glued to the name
    /// (<c>"token"</c>, <c>**Password**</c>), then blanks, and a table's bar.</summary>
    private static int SkipClosers(string text, int at, out bool sawPipe)
    {
        sawPipe = false;
        for (var glued = 0; glued < 4 && at < text.Length && text[at] is '"' or '\'' or '`' or '*'; glued++) at++;
        for (var steps = 0; steps < 8 && at < text.Length; steps++)
        {
            var c = text[at];
            if (c == '|') sawPipe = true;
            else if (!IsBlank(c)) break;
            at++;
        }
        return at;
    }

    /// <summary>What may sit between the separator and the value: blanks, Markdown emphasis, a bar.</summary>
    private static int SkipLeaders(string text, int at)
    {
        for (var steps = 0; steps < 8 && at < text.Length; steps++)
        {
            var c = text[at];
            if (!(c is '*' or '|' || IsBlank(c))) break;
            at++;
        }
        return at;
    }

    /// <summary>Up to three of "now", "currently", "changed", "to" …: "is now X", "was changed to X".</summary>
    private static int SkipFillers(string text, int at)
    {
        for (var hops = 0; hops < 3; hops++)
        {
            var start = SkipBlanks(text, at);
            var length = WordLength(text, start);
            if (length == 0 || !Fillers.Contains(text.Substring(start, length).ToLowerInvariant())) return at;
            var end = start + length;
            if (end < text.Length && !IsBlank(text[end])) return at;
            at = end;
        }
        return at;
    }

    private static int SkipNameSuffix(string text, int at)
    {
        for (var hops = 0; hops < MaxSuffixWords; hops++)
        {
            var word = at;
            var separated = false;
            if (word < text.Length && text[word] is '_' or '-' or '.')
            {
                word++;
                separated = true;
            }
            var length = SuffixWordLength(text, word, separated);
            if (length == 0) return at;
            at = word + length;
        }
        return at;
    }

    private static int SuffixWordLength(string text, int at, bool separated)
    {
        if (at >= text.Length) return 0;
        if (char.IsDigit(text[at]))
        {
            var digits = at;
            while (digits < text.Length && char.IsDigit(text[digits])) digits++;
            return digits - at;
        }
        // (Joined without a separator, a word must begin a camelCase hump: secretKey, not secretary.)
        if (!separated && !char.IsUpper(text[at])) return 0;
        foreach (var word in SuffixWords)
        {
            if (!text.AsSpan(at).StartsWith(word, StringComparison.OrdinalIgnoreCase)) continue;
            var next = at + word.Length;
            if (next < text.Length && char.IsLower(text[next])) continue; // "keychain" is not "key"
            return word.Length;
        }
        return 0;
    }

    /// <summary>One or two words of a name we do not know after the keyword, joined with _ - or . — TOKEN_BEDROCK, API_KEY_OPENROUTER.
    /// <c>Descriptive</c>: one of them says what the secret is about (SECRET_ID, TOKEN_FILE), so a value here is not it.</summary>
    private static (int End, bool Descriptive)? GenericTail(string text, int at)
    {
        var end = at;
        var descriptive = false;
        for (var hops = 0; hops < 2; hops++)
        {
            if (end >= text.Length || text[end] is not ('_' or '-' or '.')) break;
            var word = end + 1;
            var stop = word;
            while (stop < text.Length && stop - word < 24 && char.IsLetterOrDigit(text[stop])) stop++;
            if (stop == word) break;
            if (Descriptors.Contains(text.Substring(word, stop - word).ToLowerInvariant())) descriptive = true;
            end = stop;
        }
        return end > at ? (end, descriptive) : null;
    }

    /// <summary>A word of ordinary prose: letters, with hyphens and apostrophes inside.</summary>
    private static int WordLength(string text, int at)
    {
        var end = at;
        while (end < text.Length && end - at < 24 && (char.IsLetter(text[end]) || (end > at && text[end] is '\'' or '’' or '-'))) end++;
        return end - at;
    }

    /// <summary>The value after a separator; when the line ends there, the first word of the next line — unless that is another
    /// key or a list item ("password:" newline "Xk29mQ788abZ").</summary>
    private static string? ReadValueAfterSeparator(string text, int at)
    {
        if (at < text.Length && text[at] is not ('\n' or '\r')) return ReadValue(text, at);
        if (at >= text.Length) return null;
        var next = at;
        if (text[next] == '\r') next++;
        if (next < text.Length && text[next] == '\n') next++;
        next = SkipBlanks(text, next);
        if (next >= text.Length || text[next] is '-' or '#' or '}' or ']' or ')' or '/' or '\n' or '\r') return null;
        for (var probe = next; probe < text.Length && probe - next < 40 && !char.IsWhiteSpace(text[probe]); probe++)
        {
            if (text[probe] is ':' or '=' or '：' or '＝') return null; // another "key: value" line
        }
        return ReadValue(text, next);
    }

    /// <summary>The value after the name: in quotes up to the closing quote, otherwise up to the next blank (or bar, in a table).</summary>
    private static string? ReadValue(string text, int at, bool untilPipe = false)
    {
        at = SkipBlanks(text, at);
        if (at >= text.Length) return null;
        if (text[at] is '@' or '$' && at + 1 < text.Length && text[at + 1] is '"' or '\'') at++; // C#: @"…", $"…"
        if (text[at] is '"' or '\'' or '`')
        {
            var quote = text[at];
            var triple = at + 2 < text.Length && text[at + 1] == quote && text[at + 2] == quote;
            var start = at + (triple ? 3 : 1);
            var close = triple
                ? text.IndexOf(new string(quote, 3), start, Math.Min(text.Length - start, 400), StringComparison.Ordinal)
                : text.IndexOf(quote, start, Math.Min(text.Length - start, 400));
            return close < 0 ? null : text[start..close];
        }
        var end = at;
        while (end < text.Length && end - at < 400 && !char.IsWhiteSpace(text[end]) && !(untilPipe && text[end] == '|')) end++;
        // Punctuation that ends a sentence or a list item is not part of the value.
        return text[at..end].TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '"', '\'', '`', '*');
    }

    /// <summary>Whether a value given to a secret-sounding name is itself a secret rather than a word, a sentence, a path, a
    /// reference to where the secret lives, a date, or a stand-in. <paramref name="strict"/>: the link between name and value is
    /// a guess, so the value has to look random as well.</summary>
    private static bool LooksLikeLiteral(string value, Strength strength, bool strict = false)
    {
        value = value.Trim();
        if (value.Length < (strength == Strength.Weak ? 8 : 6)) return false;
        // A secret has no spaces to speak of: "Enter your password here" and "Minimum 8 characters" are text.
        if (value.Count(char.IsWhiteSpace) >= 2) return false;
        if (IsReferenceOrPlaceholder(value) || (TechnicalWord.IsMatchSafe(value) && !AlphanumericRun.IsMatchSafe(value))) return false; // ("base64:" + a key is a key)
        if (Metadata.IsMatchSafe(value)) return false; // a date, a duration, a version, a key algorithm
        if (NamesASecret(value)) return false; // token: accessToken — another variable, not a value
        if (strict) return LooksRandom(value);

        var hasDigit = false;
        var hasLetter = false;
        var hasUpper = false;
        var hasLower = false;
        var hasSymbol = false;
        foreach (var c in value)
        {
            if (char.IsDigit(c)) hasDigit = true;
            else if (char.IsLetter(c))
            {
                hasLetter = true;
                if (char.IsUpper(c)) hasUpper = true;
                else hasLower = true;
            }
            else if ("!@#$%^&*+=~".Contains(c)) hasSymbol = true;
        }
        if (hasDigit && hasLetter) return true; // hunter2, Xk29mQ788abZ
        if (hasSymbol && (hasLetter || hasDigit)) return true; // P@ssw0rd-style
        if (hasDigit) return strength == Strength.Strong || value.Length >= 16; // "password: 123456" — but not "token limit: 128000"
        // a long random string of letters — not a chain of calls or a list
        return value.Length >= 20 && hasUpper && hasLower && !value.Contains(' ') && !value.Contains('/') && value.IndexOfAny(CodePunctuation) < 0;
    }

    private static readonly char[] CodePunctuation = ['(', ')', '[', ']', '{', '}', '<', '>', ';', ','];

    /// <summary>A plain identifier that itself says "token", "secret", "password" or "key": <c>accessToken</c>, <c>client_secret</c>.
    /// (With a digit in it, it may be a password: password123.)</summary>
    private static bool NamesASecret(string value) =>
        value.All(c => char.IsLetter(c) || c == '_') && (Keyword.IsMatchSafe(value) || value.Contains("key", StringComparison.OrdinalIgnoreCase));

    /// <summary>Random enough to be a key: long, made of what keys are made of, with digits mixed in among letters or capitals
    /// among lower case case after case — not "prod-db-creds-v3", "argon2id" or "X-Amz-Content-SHA256".</summary>
    private static bool LooksRandom(string value)
    {
        if (value.Length < 10 || value.Length > 400) return false;
        int letters = 0, digits = 0, letterDigit = 0, caseFlips = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsAsciiLetter(c)) letters++;
            else if (char.IsAsciiDigit(c)) digits++;
            else if ("+/=_-.:!@#$%^&*~".IndexOf(c) < 0) return false;
            if (i == 0) continue;
            var p = value[i - 1];
            if ((char.IsAsciiLetter(c) && char.IsAsciiDigit(p)) || (char.IsAsciiDigit(c) && char.IsAsciiLetter(p))) letterDigit++;
            else if ((char.IsAsciiLetterUpper(c) && char.IsAsciiLetterLower(p)) || (char.IsAsciiLetterLower(c) && char.IsAsciiLetterUpper(p))) caseFlips++;
        }
        if (letters == 0 || digits == 0) return false;
        return letterDigit >= 4 || caseFlips >= 5;
    }

    private static readonly Regex DottedChain = new(@"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+;?$", RegexOptions.CultureInvariant, Limit);
    /// <summary>A call, an index or a generic in code: <c>get_token(user)</c>, <c>configuration.GetValue&lt;string&gt;(…)</c>, <c>hashlib.sha256(raw)</c>.</summary>
    private static readonly Regex CodeCall = new(@"^[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*(?:[(\[]|<\w\S*>)", RegexOptions.CultureInvariant, Limit);
    private static readonly Regex OnlyDigitsLeft = new(@"^\d+[)\]>]?;?$", RegexOptions.CultureInvariant, Limit);
    private static readonly Regex EnvironmentName = new(@"^[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+$", RegexOptions.CultureInvariant, Limit);
    private static readonly Regex DigitRun = new(@"\d{3,}", RegexOptions.CultureInvariant, Limit);
    private static readonly Regex AlphanumericRun = new(@"[A-Za-z0-9]{16,}", RegexOptions.CultureInvariant, Limit);
    /// <summary>What documentation puts as a value: AKIAIOSFODNN7EXAMPLE, wJalr…EXAMPLEKEY, xxxxxxxx, your-token-here. (EXAMPLE in
    /// capitals only: "Example1234!" is a password someone chose.)</summary>
    private static readonly Regex DocumentationValue = new(@"EXAMPLE|(?i:x{6,}|placeholder|redacted|(?:^|[-_.])your(?:[-_.]|$))", RegexOptions.CultureInvariant, Limit);

    private static readonly Regex StandIn = new(@"^(?:changeme|(?:your|example|sample|dummy|fake|placeholder|redacted|replace|insert|todo|xxx+)(?:[-_. ]|$)|(?:my|the|an?|some) )",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    /// <summary>Words that carry a digit without being a secret: base64, sha256, oauth2, 1Password, k8s, utf-8, http2, tls12…</summary>
    private static readonly Regex TechnicalWord = new(
        @"^(?:1password|k8s|s3|x86|x64|utf-?8|base64(?:url)?|sha\d+|aes\d*|md5|oauth\d?|rs\d+|hs\d+|ipv[46]|http[23]|tls\d*|python\d*|node\d*|net\d+)(?:[-_.:/,]|$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    /// <summary>A date, a duration, a version and a key algorithm carry digits and are not secrets: 2024-01-15, 3600ms, 24hours, v1.2.3, Ed25519.</summary>
    private static readonly Regex Metadata = new(
        @"^(?:\d{4}-\d{2}-\d{2}(?:[T ].*)?|\d+(?:\.\d+)?(?:ms|us|µs|ns|s|secs?|seconds?|m|mins?|minutes?|h|hrs?|hours?|d|days?|w|weeks?)|v?\d+(?:\.\d+){1,3}(?:[-+][\w.]+)?|(?:ed25519|ed448|x25519|rsa[-_]?\d*|ecdsa[-_\w]*|aes[-_]?\d*[-_\w]*|hmac[-_\w]*|sha\d+))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    /// <summary>A reference in full: ${VAR}, $VAR, $(cmd), %VAR%, {{ x }}, &lt;x&gt;, [x], @scope/pkg, ~/x, /x, \\server\share, C:\x, ./x,
    /// env:NAME, arn:aws:… — but not a password that merely starts with $ or % or @ ("$ecureP4ss!", "%Xk29mQ788abZ").</summary>
    private static readonly Regex WholeReference = new(
        @"^(?:\$\{[^}]*\}?$|\$\([^)]*\)?$|(?i:\$env:)\w+$|\$(?:[A-Za-z_]+|[A-Z][A-Z0-9_]*|[a-z][a-z0-9_]*)$|%[A-Za-z_]\w*%$|\{\{|\{%|\{[^{}]*\}$|\{[A-Za-z$]|<[^<>]*>$|<[A-Za-z]|\[[^\[\]]*\]$|\[[A-Za-z]|\([^()]*\)$|\([A-Za-z]|@[\w.\-]+/[\w.\-/]+$|~[/\\]|[/\\]|\.{1,2}[/\\]|[A-Za-z]:[/\\]|(?i:(?:env|file|vault|op|ssm|secret|secrets|kms|ref|secretref|keyvault|arn):))",
        RegexOptions.CultureInvariant, Limit);

    private static bool LooksLikeCode(string value)
    {
        try
        {
            var call = CodeCall.Match(value);
            return call.Success && !OnlyDigitsLeft.IsMatch(value[call.Length..]);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static bool IsMatchSafe(this Regex pattern, string value)
    {
        try
        {
            return pattern.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Something that points at a secret (a variable, a path, a URL, an expression) or stands in for one, so is not one.</summary>
    private static bool IsReferenceOrPlaceholder(string value)
    {
        if (value.Length == 0) return true;
        if (WholeReference.IsMatchSafe(value)) return true;
        if (value.Contains("...", StringComparison.Ordinal) || value.Contains('…')) return true; // elided: "123456:ABC..."
        if (value.Contains("://", StringComparison.Ordinal)) return true;
        // Already encrypted (an Ansible vault tag, a sops value): not the secret itself.
        if (value.StartsWith("!vault", StringComparison.OrdinalIgnoreCase) || value.StartsWith("ENC[", StringComparison.Ordinal)) return true;
        if (value.All(c => c is 'x' or 'X' or '*' or '.' or '-' or '_' or '•' or '#')) return true; // masked: ****, xxxxxx
        if (DocumentationValue.IsMatchSafe(value)) return true;
        // A call or an index has names in it (Environment.GetEnvironmentVariable("X"), hashlib.sha256(raw)) — unless all that
        // follows the bracket is a number: Hunter2(2024) is a password.
        if (LooksLikeCode(value)) return true;
        // A long run of letters and digits is what a key looks like, whatever dots or brackets are around it (SendGrid keys are SG.<22>.<43>).
        if (AlphanumericRun.IsMatchSafe(value)) return StandIn.IsMatchSafe(value);
        if (DottedChain.IsMatchSafe(value) || StandIn.IsMatchSafe(value)) return true;
        return EnvironmentName.IsMatchSafe(value) && !DigitRun.IsMatchSafe(value); // OPENAI_API_KEY, not ABCD1234_EFGH5678
    }

    // MARK: The same, the way config files and code write it

    private const string SecretWords = @"(?:password|passwd|passphrase|secret|token|api[_\-]?key|private[_\-]?key)";

    /// <summary>A name and its <c>value</c> side by side (Kubernetes <c>- name: DB_PASSWORD / value: …</c>, ECS and docker inspect JSON,
    /// NuGet <c>&lt;add key="ClearTextPassword" value="…"/&gt;</c>).</summary>
    private static readonly Regex NameAndValue = new(
        @"\b(?:name|key)\b[""']?\s*[:=]\s*[""']?(?<n>[\w.\-]{0,60}" + SecretWords + @"[\w.\-]{0,40})[""']?[\s,;]{1,40}(?:-\s*)?[""']?value[""']?\s*[:=]\s*[""']?(?<v>[^\s""',}<]{6,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    /// <summary>An XML element named for a secret: <c>&lt;password&gt;…&lt;/password&gt;</c>.</summary>
    private static readonly Regex XmlElement = new(
        @"<(?<n>[\w.\-:]{0,40}?" + SecretWords + @"[\w.\-:]{0,40})>\s*(?<v>[^<\s]{6,})\s*</",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    /// <summary>A quoted name given a quoted value: <c>os.environ["DB_PASSWORD"] = "…"</c>, <c>SetEnvironmentVariable("DB_PASSWORD", "…")</c>,
    /// <c>conf.set("db.password", "…")</c>.</summary>
    private static readonly Regex QuotedPair = new(
        @"[""'](?<n>[\w.\-]{0,60}" + SecretWords + @"[\w.\-]{0,40})[""']\s*[\]\)]?\s*(?<s>[,=])\s*@?[""'](?<v>[^""'\s]{6,})[""']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    /// <summary>The command lines that take a password: <c>curl -u user:password</c>, <c>docker login -p …</c>, <c>mysql -pPASSWORD</c>.</summary>
    private static readonly Regex[] CommandLines =
    [
        new(@"\bcurl\b[^\n]{0,200}?\s(?:-u|--user)[ =]+[^\s:'""]{1,64}:(?<v>[^\s'""]{6,})", RegexOptions.CultureInvariant, Limit),
        new(@"\b(?:docker|podman|helm|oras|skopeo|buildah)\b[^\n]{0,200}?\s(?:-p|--password)[ =]+(?<v>[^\s'""]{6,})", RegexOptions.CultureInvariant, Limit),
        new(@"\bmysql(?:admin|dump)?\b[^\n]{0,200}?\s-p(?<v>[^\s'""\-][^\s'""]{5,})", RegexOptions.CultureInvariant, Limit),
    ];

    /// <summary>The name of something that describes a secret (SECRET_NAME, TokenFile) or pages through them (NextToken) rather than being one.</summary>
    private static bool IsAboutASecret(string name)
    {
        if (NonCredentialTokenName.IsMatchSafe(name)) return true;
        var start = 0;
        for (var i = 0; i <= name.Length; i++)
        {
            var wordEnds = i == name.Length || !char.IsLetterOrDigit(name[i]) || (i > start && char.IsUpper(name[i]) && char.IsLower(name[i - 1]));
            if (!wordEnds) continue;
            if (i > start && Descriptors.Contains(name[start..i].ToLowerInvariant())) return true;
            start = i < name.Length && char.IsLetterOrDigit(name[i]) ? i : i + 1;
        }
        return false;
    }

    private static readonly Regex NonCredentialTokenName = new(NotCredentialWords + @"[_\-.]?(?:token|secret)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    private static bool HasStructuredSecret(string text)
    {
        try
        {
            foreach (var pattern in new[] { NameAndValue, XmlElement, QuotedPair })
            {
                foreach (Match match in pattern.Matches(text))
                {
                    if (IsAboutASecret(match.Groups["n"].Value)) continue;
                    // ("name", "value") side by side is a guess — it is as often two words in a list or two arguments of a call.
                    var guessed = match.Groups["s"].Value == ",";
                    if (LooksLikeLiteral(match.Groups["v"].Value, Strength.Medium, guessed)) return true;
                }
            }
            foreach (var pattern in CommandLines)
            {
                foreach (Match match in pattern.Matches(text))
                {
                    if (LooksLikeLiteral(match.Groups["v"].Value, Strength.Strong)) return true;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return true; // text that defeats a pattern is not text to keep
        }
        return false;
    }
}
