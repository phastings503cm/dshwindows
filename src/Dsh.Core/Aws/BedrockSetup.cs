using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Dsh.Core;

// MARK: - Amazon Bedrock account setup
//
// Everything between "signed in to AWS" and "the model answers", done with the AWS CLI against the Bedrock
// control plane (botocore bedrock/2023-04-20):
//   * which models this Region offers (ListFoundationModels) and the id to call each by — the model id
//     when it runs on demand, otherwise a cross-Region inference profile (ListInferenceProfiles);
//   * whether this account may use a model (GetFoundationModelAvailability: agreement, authorization,
//     entitlement, Region);
//   * Anthropic's one-time "first time use" form (Get/PutUseCaseForModelAccess);
//   * a Marketplace model's terms — EULA link, prices, refund policy — to show and accept
//     (ListFoundationModelAgreementOffers, CreateFoundationModelAgreement), and waiting while AWS
//     finishes the subscription (it can take up to about 15 minutes).
// Since October 2025 serverless models are enabled by default in commercial Regions and Marketplace models
// subscribe on first use, so for most accounts this reports "ready" straight away; the rest is for the
// accounts where it doesn't.

// MARK: - Regions

/// <summary>A Region to offer in the guide's picker.</summary>
public sealed record AwsRegionChoice(string Code, string Name)
{
    /// <summary>"US East (N. Virginia) — us-east-1".</summary>
    public string Display => $"{Name} — {Code}";
}

/// <summary>Bedrock Regions most people pick from.</summary>
public static class BedrockRegions
{
    /// <summary>The Region with the most models (and the one `aws login` itself suggests).</summary>
    public const string Default = "us-east-1";

    public static IReadOnlyList<AwsRegionChoice> Common { get; } =
    [
        new("us-east-1", "US East (N. Virginia)"),
        new("us-east-2", "US East (Ohio)"),
        new("us-west-2", "US West (Oregon)"),
        new("ca-central-1", "Canada (Central)"),
        new("sa-east-1", "South America (São Paulo)"),
        new("eu-central-1", "Europe (Frankfurt)"),
        new("eu-west-1", "Europe (Ireland)"),
        new("eu-west-2", "Europe (London)"),
        new("eu-west-3", "Europe (Paris)"),
        new("eu-north-1", "Europe (Stockholm)"),
        new("ap-northeast-1", "Asia Pacific (Tokyo)"),
        new("ap-northeast-2", "Asia Pacific (Seoul)"),
        new("ap-south-1", "Asia Pacific (Mumbai)"),
        new("ap-southeast-1", "Asia Pacific (Singapore)"),
        new("ap-southeast-2", "Asia Pacific (Sydney)"),
    ];

    /// <summary>"Europe (Frankfurt)" for "eu-central-1"; the code itself for Regions not listed.</summary>
    public static string NameOf(string region) => Common.FirstOrDefault(r => r.Code == region)?.Name ?? region;

    /// <summary>Inference-profile prefixes to prefer from <paramref name="region"/>, closest geography
    /// first: a US Region prefers "us." profiles (requests stay in US Regions), then "global.". Profiles
    /// listed in a Region are all callable from it; this only decides which to pick when several are.</summary>
    public static IReadOnlyList<string> ProfilePrefixes(string region)
    {
        var r = region.ToLowerInvariant();
        if (r.StartsWith("us-gov-", StringComparison.Ordinal)) return ["us-gov"];
        if (r.StartsWith("us-", StringComparison.Ordinal)) return ["us", "global"];
        if (r.StartsWith("ca-", StringComparison.Ordinal)) return ["ca", "us", "global"];
        if (r.StartsWith("eu-", StringComparison.Ordinal)) return ["eu", "global"];
        if (r is "ap-northeast-1" or "ap-northeast-3") return ["jp", "apac", "global"];
        if (r is "ap-southeast-2" or "ap-southeast-4") return ["au", "apac", "global"];
        if (r.StartsWith("ap-", StringComparison.Ordinal)) return ["apac", "global"];
        return ["global"];
    }
}

// MARK: - Models

/// <summary>A Bedrock model DSH can chat with.</summary>
/// <param name="ModelId">The base model id ("anthropic.claude-sonnet-4-5-20250929-v1:0"); access checks use it.</param>
/// <param name="InvokeId">What to send as the model when chatting: <paramref name="ModelId"/> for on-demand
/// models, else an inference profile id ("us.anthropic…"); null when it can't be called in this Region.</param>
/// <param name="LifecycleStatus">"ACTIVE" (only those are listed).</param>
/// <param name="Recommended">The newest of a family DSH suggests for coding (Claude Sonnet, Opus, Haiku,
/// Nova Pro, Llama, Mistral, DeepSeek, Qwen, gpt-oss).</param>
public sealed record BedrockModel(
    string ModelId, string Name, string Provider, IReadOnlyList<string> InputModalities, bool StreamingSupported,
    string LifecycleStatus, IReadOnlyList<string> InferenceTypes, string? InvokeId, bool Recommended = false)
{
    public bool AcceptsImages => InputModalities.Contains("IMAGE", StringComparer.OrdinalIgnoreCase);
    public bool CanInvoke => InvokeId is not null;
    public bool UsesInferenceProfile => InvokeId is not null && InvokeId != ModelId;
    /// <summary>Anthropic models need the one-time use-case form.</summary>
    public bool IsAnthropic => ModelId.StartsWith("anthropic.", StringComparison.OrdinalIgnoreCase)
                               || Provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase);
    /// <summary>"Claude Sonnet 4.5 (Anthropic)".</summary>
    public string DisplayName => Provider.Length > 0 ? $"{Name} ({Provider})" : Name;
}

/// <summary>Turning the CLI's model and inference-profile listings into <see cref="BedrockModel"/>s.</summary>
public static partial class BedrockCatalog
{
    private sealed record Profile(string Id, string Prefix, HashSet<string> ModelIds);

    /// <summary>Model families to suggest, in the order shown. Newest of each is "recommended".</summary>
    private static readonly (string Family, Regex Pattern)[] Families =
    [
        ("Claude Sonnet", new Regex(@"^anthropic\.claude-(?:[\d-]+-)?sonnet", RegexOptions.CultureInvariant)),
        ("Claude Opus", new Regex(@"^anthropic\.claude-(?:[\d-]+-)?opus", RegexOptions.CultureInvariant)),
        ("Claude Haiku", new Regex(@"^anthropic\.claude-(?:[\d-]+-)?haiku", RegexOptions.CultureInvariant)),
        ("Amazon Nova Pro", new Regex(@"^amazon\.nova-(?:\d+-)?pro", RegexOptions.CultureInvariant)),
        ("Llama", new Regex(@"^meta\.llama", RegexOptions.CultureInvariant)),
        ("Mistral", new Regex(@"^mistral\.", RegexOptions.CultureInvariant)),
        ("DeepSeek", new Regex(@"^deepseek\.", RegexOptions.CultureInvariant)),
        ("Qwen", new Regex(@"^qwen\.", RegexOptions.CultureInvariant)),
        ("gpt-oss", new Regex(@"^openai\.gpt-oss", RegexOptions.CultureInvariant)),
    ];

    /// <summary>Build the model list from `bedrock list-foundation-models` and `bedrock
    /// list-inference-profiles` output: ACTIVE models that stream text, each with the id to call it by in
    /// <paramref name="region"/>. Variants that only run on provisioned throughput (and no profile covers)
    /// are left out. Sorted: callable first, then the suggested families, then by provider and name.</summary>
    public static IReadOnlyList<BedrockModel> Build(JsonNode? foundationModels, JsonNode? inferenceProfiles, string region)
    {
        var profiles = ParseProfiles(inferenceProfiles);
        var models = new List<BedrockModel>();
        foreach (var node in foundationModels?["modelSummaries"] as JsonArray ?? [])
        {
            if (node is not JsonObject obj || JsonArgs.String(obj, "modelId") is not { Length: > 0 } id) continue;
            var lifecycle = obj["modelLifecycle"] is JsonObject life ? JsonArgs.String(life, "status") ?? "" : "";
            var outputs = Strings(obj["outputModalities"]);
            var streaming = JsonArgs.Bool(obj, "responseStreamingSupported", false);
            if (!lifecycle.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) || !streaming
                || !outputs.Contains("TEXT", StringComparer.OrdinalIgnoreCase)) continue;
            var inference = Strings(obj["inferenceTypesSupported"]);
            var invokeId = ChooseInvokeId(id, inference, profiles, region);
            if (invokeId is null && inference.Count > 0 && inference.All(t => t.Equals("PROVISIONED", StringComparison.OrdinalIgnoreCase))) continue;
            models.Add(new BedrockModel(id, JsonArgs.String(obj, "modelName") ?? id, JsonArgs.String(obj, "providerName") ?? "",
                Strings(obj["inputModalities"]), streaming, lifecycle.ToUpperInvariant(), inference, invokeId));
        }

        // The newest callable model of each family is recommended.
        var recommended = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in models.Where(m => m.CanInvoke).GroupBy(m => FamilyIndex(m.ModelId)).Where(g => g.Key < Families.Length))
            recommended.Add(group.OrderByDescending(m => m.ModelId, RecencyComparer.Instance).First().ModelId);

        return models
            .Select(m => recommended.Contains(m.ModelId) ? m with { Recommended = true } : m)
            .OrderBy(m => m.CanInvoke ? 0 : 1)
            .ThenBy(m => m.Recommended ? 0 : 1)
            .ThenBy(m => FamilyIndex(m.ModelId))
            .ThenByDescending(m => m.ModelId, RecencyComparer.Instance)
            .ThenBy(m => m.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The id to call <paramref name="modelId"/> by in <paramref name="region"/>: itself when it
    /// supports on-demand calls, else the system inference profile for it whose prefix comes first in
    /// <see cref="BedrockRegions.ProfilePrefixes"/> (any other listed one after those), else null.</summary>
    public static string? ChooseInvokeId(string modelId, IReadOnlyList<string> inferenceTypes, JsonNode? inferenceProfiles, string region) =>
        ChooseInvokeId(modelId, inferenceTypes, ParseProfiles(inferenceProfiles), region);

    private static string? ChooseInvokeId(string modelId, IReadOnlyList<string> inferenceTypes, IReadOnlyList<Profile> profiles, string region)
    {
        if (inferenceTypes.Contains("ON_DEMAND", StringComparer.OrdinalIgnoreCase)) return modelId;
        var preferred = BedrockRegions.ProfilePrefixes(region);
        return profiles
            .Where(p => p.ModelIds.Contains(modelId) || p.Id.EndsWith("." + modelId, StringComparison.Ordinal))
            .OrderBy(p => preferred.Contains(p.Prefix) ? preferred.TakeWhile(x => x != p.Prefix).Count() : preferred.Count)
            .Select(p => p.Id)
            .FirstOrDefault();
    }

    private static List<Profile> ParseProfiles(JsonNode? inferenceProfiles)
    {
        var list = new List<Profile>();
        foreach (var node in inferenceProfiles?["inferenceProfileSummaries"] as JsonArray ?? [])
        {
            if (node is not JsonObject obj || JsonArgs.String(obj, "inferenceProfileId") is not { Length: > 0 } id) continue;
            if (JsonArgs.String(obj, "status") is { } status && !status.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase)) continue;
            if (JsonArgs.String(obj, "type") is { } type && !type.Equals("SYSTEM_DEFINED", StringComparison.OrdinalIgnoreCase)) continue;
            var modelIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in obj["models"] as JsonArray ?? [])
            {
                if (model is JsonObject m && JsonArgs.String(m, "modelArn") is { } arn
                    && arn.IndexOf("foundation-model/", StringComparison.Ordinal) is var at and >= 0)
                    modelIds.Add(arn[(at + "foundation-model/".Length)..]);
            }
            var dot = id.IndexOf('.');
            list.Add(new Profile(id, dot > 0 ? id[..dot] : "", modelIds));
        }
        return list;
    }

    private static List<string> Strings(JsonNode? node) =>
        (node as JsonArray ?? []).Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList();

    private static int FamilyIndex(string modelId)
    {
        for (var i = 0; i < Families.Length; i++)
            if (Families[i].Pattern.IsMatch(modelId)) return i;
        return Families.Length;
    }

    /// <summary>The family a model belongs to ("Claude Sonnet"), or null.</summary>
    public static string? FamilyOf(string modelId) => FamilyIndex(modelId) is var i && i < Families.Length ? Families[i].Family : null;

    /// <summary>Orders model ids oldest to newest within a family: first the version numbers in the name
    /// (claude-sonnet-4-5 is 4.5, newer than claude-sonnet-4 and claude-3-7-sonnet), then the release date
    /// (YYYYMMDD), then the "-v2" revision.</summary>
    public sealed partial class RecencyComparer : IComparer<string>
    {
        public static readonly RecencyComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var (xv, xd, xr) = Key(x ?? "");
            var (yv, yd, yr) = Key(y ?? "");
            for (var i = 0; i < Math.Max(xv.Count, yv.Count); i++)
            {
                var c = (i < xv.Count ? xv[i] : 0).CompareTo(i < yv.Count ? yv[i] : 0);
                if (c != 0) return c;
            }
            var d = xd.CompareTo(yd);
            return d != 0 ? d : xr.CompareTo(yr);
        }

        private static (List<long> Version, long Date, long Revision) Key(string modelId)
        {
            var id = modelId.Contains('.') ? modelId[(modelId.IndexOf('.') + 1)..] : modelId;
            long revision = 0;
            if (SuffixRegex().Match(id) is { Success: true } suffix)
            {
                revision = long.TryParse(suffix.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : 0;
                id = id[..suffix.Index];
            }
            long date = 0;
            var version = new List<long>();
            foreach (Match number in NumberRegex().Matches(id))
            {
                if (!long.TryParse(number.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) continue;
                if (number.Value.Length == 8 && number.Value.StartsWith("20", StringComparison.Ordinal)) date = n;
                else if (date == 0) version.Add(n);
            }
            return (version, date, revision);
        }

        /// <summary>"-v1:0" or "-1:0" at the end.</summary>
        [GeneratedRegex(@"-v?(\d+)(?::[0-9a-z]+)*$", RegexOptions.CultureInvariant)]
        private static partial Regex SuffixRegex();

        [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
        private static partial Regex NumberRegex();
    }
}

// MARK: - Access

public enum ModelAccessState
{
    /// <summary>This account can use the model now.</summary>
    Ready,
    /// <summary>Anthropic's one-time use-case form hasn't been submitted for this account.</summary>
    NeedsUseCaseForm,
    /// <summary>A Marketplace model whose terms haven't been accepted (<see cref="ModelAccess.Offer"/> has them).</summary>
    NeedsAgreement,
    /// <summary>AWS is still setting up the subscription (up to about 15 minutes).</summary>
    Pending,
    /// <summary>The model isn't offered in this Region.</summary>
    NotInRegion,
    /// <summary>An IAM policy doesn't allow it (<see cref="ModelAccess.MissingPermissions"/> says what).</summary>
    NotAuthorized,
    Error,
}

/// <summary>One price in a Marketplace offer's rate card.</summary>
public sealed record ModelPrice(string Dimension, string Price, string? Unit, string? Description)
{
    /// <summary>"Input tokens (per 1K): US$0.003".</summary>
    public string Display
    {
        get
        {
            var label = string.IsNullOrWhiteSpace(Description) ? Dimension : Description!;
            var unit = string.IsNullOrWhiteSpace(Unit) || Unit!.Equals("Units", StringComparison.OrdinalIgnoreCase) ? "" : $" per {Unit}";
            return $"{label}: US${Price}{unit}";
        }
    }
}

/// <summary>The terms a Marketplace model is sold on — what the guide shows before "Accept".</summary>
/// <param name="OfferToken">Passed back to accept this offer.</param>
/// <param name="LegalTermsUrl">The EULA.</param>
/// <param name="AgreementDuration">As AWS words it (often ISO 8601, e.g. "P1Y"); see <see cref="DurationText"/>.</param>
public sealed record ModelOffer(string OfferToken, string? OfferId, string? LegalTermsUrl, IReadOnlyList<ModelPrice> Prices,
                                string? RefundPolicy, string? AgreementDuration)
{
    /// <summary>"Input tokens: US$0.003; Output tokens: US$0.015", or a note that none are listed.</summary>
    public string PricingSummary => Prices.Count == 0
        ? "No usage prices are listed in the offer; see the model's page in the AWS console."
        : string.Join("; ", Prices.Select(p => p.Display));

    /// <summary>"1 year" for "P1Y"; other texts as given.</summary>
    public string? DurationText => AgreementDuration is null ? null : BedrockSetup.FriendlyDuration(AgreementDuration);
}

/// <summary>What GetFoundationModelAvailability said.</summary>
public sealed record BedrockAvailability(string Agreement, string? AgreementError, string Authorization, string Entitlement, string Region);

/// <summary>Whether this account can use a model, and what to do if not.</summary>
public sealed record ModelAccess(
    ModelAccessState State, string ModelId, string Message, ModelOffer? Offer = null,
    IReadOnlyList<string>? MissingPermissions = null, bool? UseCaseSubmitted = null, BedrockAvailability? Availability = null)
{
    public bool IsReady => State == ModelAccessState.Ready;
    /// <summary>DSH can accept the terms itself (there is an offer to accept).</summary>
    public bool CanAcceptInDsh => State == ModelAccessState.NeedsAgreement && Offer is not null;
}

// MARK: - Anthropic's use-case form

/// <summary>Who will use the models ("intendedUsers" in the form). "0" = the company's own staff is
/// what AWS's examples use; 1 and 2 follow the console's order (external, both) but are not documented.</summary>
public enum UseCaseAudience
{
    Internal = 0,
    External = 1,
    Both = 2,
}

/// <summary>Anthropic's "first time use" form, submitted once per AWS account (or organization) before
/// Claude models can be used. AWS keeps it as the JSON of <see cref="ToJson"/>; it can't be changed later.</summary>
public sealed record UseCaseForm(string CompanyName, string CompanyWebsite, string Industry, string UseCases,
                                 UseCaseAudience IntendedUsers = UseCaseAudience.Internal, string OtherIndustry = "")
{
    /// <summary>Industries to offer. AWS doesn't publish the accepted list; "Energy" and "Government"
    /// appear in public examples, the rest follow the console's wording as best known. "Other" sends the
    /// text in <see cref="OtherIndustry"/>.</summary>
    public static IReadOnlyList<string> Industries { get; } =
    [
        "Technology", "Financial Services", "Healthcare", "Education", "Government", "Energy", "Retail",
        "Manufacturing", "Media and Entertainment", "Telecommunications", "Legal", "Other",
    ];

    /// <summary>What's missing, in words, or null when the form can be sent.</summary>
    public string? Problem
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CompanyName)) return "Enter your company or organization name (or your own name).";
            if (!Uri.TryCreate(NormalizedWebsite, UriKind.Absolute, out var site) || site.Scheme is not ("https" or "http") || !site.Host.Contains('.'))
                return "Enter your company's website, like https://example.com.";
            if (string.IsNullOrWhiteSpace(Industry)) return "Choose an industry.";
            if (Industry.Equals("Other", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(OtherIndustry)) return "Describe your industry.";
            if (string.IsNullOrWhiteSpace(UseCases)) return "Describe briefly what you'll use the models for.";
            if (Encoding.UTF8.GetByteCount(ToJson()) > 16384) return "The description is too long; shorten it.";
            return null;
        }
    }

    /// <summary>"example.com" → "https://example.com".</summary>
    public string NormalizedWebsite
    {
        get
        {
            var site = CompanyWebsite.Trim();
            return site.Contains("://", StringComparison.Ordinal) || site.Length == 0 ? site : "https://" + site;
        }
    }

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>{"companyName": …, "companyWebsite": …, "intendedUsers": "0", "industryOption": …,
    /// "otherIndustryOption": …, "useCases": …}, compact, in that order, every value a string.</summary>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("companyName", CompanyName.Trim());
            writer.WriteString("companyWebsite", NormalizedWebsite);
            writer.WriteString("intendedUsers", ((int)IntendedUsers).ToString(CultureInfo.InvariantCulture));
            writer.WriteString("industryOption", Industry.Trim());
            writer.WriteString("otherIndustryOption", Industry.Equals("Other", StringComparison.OrdinalIgnoreCase) ? OtherIndustry.Trim() : "");
            writer.WriteString("useCases", UseCases.Trim());
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The form as the CLI takes a blob argument: base64 of the UTF-8 JSON (DSH also passes
    /// --cli-binary-format base64, so a user's raw-in-base64-out setting can't double-encode it).</summary>
    public string ToBase64() => Convert.ToBase64String(Encoding.UTF8.GetBytes(ToJson()));

    /// <summary>Read the form back from GetUseCaseForModelAccess's formData as the CLI prints it (base64).
    /// The stored value may itself be base64 again (Terraform's AWS provider decodes it twice), so up to
    /// two layers are unwrapped.</summary>
    public static UseCaseForm? FromFormData(string formData)
    {
        var text = formData.Trim();
        for (var layer = 0; layer < 3; layer++)
        {
            if (TryParse(text) is { } form) return form;
            try
            {
                text = Encoding.UTF8.GetString(Convert.FromBase64String(text)).Trim();
            }
            catch (FormatException)
            {
                return null;
            }
        }
        return null;

        static UseCaseForm? TryParse(string json)
        {
            if (!json.StartsWith('{')) return null;
            try
            {
                if (JsonNode.Parse(json) is not JsonObject obj) return null;
                var audience = JsonArgs.Int(obj, "intendedUsers", 0);
                return new UseCaseForm(JsonArgs.String(obj, "companyName") ?? "", JsonArgs.String(obj, "companyWebsite") ?? "",
                    JsonArgs.String(obj, "industryOption") ?? "", JsonArgs.String(obj, "useCases") ?? "",
                    Enum.IsDefined(typeof(UseCaseAudience), audience) ? (UseCaseAudience)audience : UseCaseAudience.Internal,
                    JsonArgs.String(obj, "otherIndustryOption") ?? "");
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

public enum UseCaseSubmission
{
    Submitted,
    /// <summary>This account already has a form on file (AWS doesn't allow changing it), so nothing was sent.</summary>
    AlreadyOnFile,
}

// MARK: - The setup calls

/// <summary>The Bedrock side of setup for one AWS CLI profile and Region.</summary>
public sealed partial class BedrockSetup
{
    private readonly AwsCli _cli;
    private readonly TimeProvider _time;

    public BedrockSetup(AwsCli cli, string profile, string region, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(region);
        _cli = cli;
        Profile = profile;
        Region = region;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The AWS CLI profile every call uses (--profile).</summary>
    public string Profile { get; }
    public string Region { get; }

    /// <summary>Same as <see cref="BedrockRegions.Common"/>.</summary>
    public static IReadOnlyList<AwsRegionChoice> CommonRegions => BedrockRegions.Common;
    public const string DefaultRegion = BedrockRegions.Default;

    private Task<JsonNode> JsonAsync(IEnumerable<string> command, CancellationToken cancellationToken) =>
        _cli.RunJsonAsync(command, Profile, Region, cancellationToken: cancellationToken);

    /// <summary>The models this Region offers for chat, each with the id to call it by
    /// (<see cref="BedrockCatalog.Build"/>). Needs bedrock:ListFoundationModels and bedrock:ListInferenceProfiles.</summary>
    public async Task<IReadOnlyList<BedrockModel>> ListModelsAsync(CancellationToken cancellationToken = default)
    {
        var models = await JsonAsync(["bedrock", "list-foundation-models", "--by-output-modality", "TEXT"], cancellationToken).ConfigureAwait(false);
        var profiles = await ListInferenceProfilesAsync(cancellationToken).ConfigureAwait(false);
        return BedrockCatalog.Build(models, profiles, Region);
    }

    /// <summary>All system inference profiles. The CLI fetches every page itself; should it ever stop
    /// early (it prints "NextToken" then), DSH continues from there.</summary>
    private async Task<JsonNode> ListInferenceProfilesAsync(CancellationToken cancellationToken)
    {
        var all = new JsonArray();
        string? next = null;
        for (var page = 0; page < 50; page++)
        {
            List<string> command = ["bedrock", "list-inference-profiles", "--type-equals", "SYSTEM_DEFINED"];
            if (next is not null) command.AddRange(["--starting-token", next]);
            var json = await JsonAsync(command, cancellationToken).ConfigureAwait(false);
            foreach (var item in json["inferenceProfileSummaries"] as JsonArray ?? []) all.Add(item?.DeepClone());
            next = json is JsonObject obj ? JsonArgs.String(obj, "NextToken") ?? JsonArgs.String(obj, "nextToken") : null;
            if (string.IsNullOrEmpty(next)) break;
        }
        return new JsonObject { ["inferenceProfileSummaries"] = all };
    }

    /// <summary>Whether this account can use <paramref name="model"/> (checked by its base model id), and
    /// if not, what's needed: the use-case form (Anthropic), accepting terms (with the offer to show), a
    /// wait, a permission, or another Region.</summary>
    public Task<ModelAccess> CheckAccessAsync(BedrockModel model, CancellationToken cancellationToken = default) =>
        CheckAccessAsync(model.ModelId, model.Name, model.IsAnthropic, cancellationToken);

    public async Task<ModelAccess> CheckAccessAsync(string modelId, string? name = null, bool? isAnthropic = null,
                                                    CancellationToken cancellationToken = default)
    {
        var shown = string.IsNullOrWhiteSpace(name) ? modelId : name;
        var anthropic = isAnthropic ?? modelId.StartsWith("anthropic.", StringComparison.OrdinalIgnoreCase);
        JsonNode json;
        try
        {
            json = await JsonAsync(["bedrock", "get-foundation-model-availability", "--model-id", modelId], cancellationToken).ConfigureAwait(false);
        }
        catch (AwsCliException ex) when (ex.Kind == AwsCliFailure.AccessDenied)
        {
            var permission = ex.Error.Permission ?? "bedrock:GetFoundationModelAvailability";
            return new ModelAccess(ModelAccessState.NotAuthorized, modelId,
                $"DSH can't check access to {shown}: your AWS permissions don't allow {permission}. Ask whoever manages your AWS account to add the policy DSH shows — or just try the model.",
                MissingPermissions: [permission]);
        }
        catch (AwsCliException ex) when (ex.Kind == AwsCliFailure.NotFound)
        {
            return new ModelAccess(ModelAccessState.NotInRegion, modelId, $"{shown} isn't offered in {BedrockRegions.NameOf(Region)}. Pick another model or Region.");
        }
        catch (AwsCliException ex)
        {
            return new ModelAccess(ModelAccessState.Error, modelId, ex.Message);
        }

        var availability = ParseAvailability(json);
        if (availability.Region.Equals("NOT_AVAILABLE", StringComparison.OrdinalIgnoreCase))
            return new ModelAccess(ModelAccessState.NotInRegion, modelId,
                $"{shown} isn't offered in {BedrockRegions.NameOf(Region)}. Pick another model or Region.", Availability: availability);
        if (availability.Authorization.Equals("NOT_AUTHORIZED", StringComparison.OrdinalIgnoreCase))
            return new ModelAccess(ModelAccessState.NotAuthorized, modelId,
                $"Your AWS permissions don't allow using {shown}. Ask whoever manages your AWS account to allow {string.Join(" and ", BedrockPermissions.InvokeActions)} — DSH can show the exact policy to send them.",
                MissingPermissions: BedrockPermissions.InvokeActions, Availability: availability);
        if (availability.Agreement.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
            return new ModelAccess(ModelAccessState.Error, modelId,
                $"AWS reported a problem with access to {shown}: {availability.AgreementError ?? "no details given"}.", Availability: availability);

        bool? useCase = null;
        if (anthropic)
        {
            useCase = await UseCaseOnFileAsync(cancellationToken).ConfigureAwait(false);
            if (useCase == false)
                return new ModelAccess(ModelAccessState.NeedsUseCaseForm, modelId,
                    "Anthropic asks each AWS account once for a few details about how its models will be used. Fill in the short form and DSH sends it for you.",
                    UseCaseSubmitted: false, Availability: availability);
        }

        if (availability.Agreement.Equals("NOT_AVAILABLE", StringComparison.OrdinalIgnoreCase))
        {
            ModelOffer? offer = null;
            IReadOnlyList<string>? missing = null;
            try
            {
                offer = await FirstOfferAsync(modelId, cancellationToken).ConfigureAwait(false);
            }
            catch (AwsCliException ex) when (ex.Kind == AwsCliFailure.AccessDenied)
            {
                missing = [ex.Error.Permission ?? "bedrock:ListFoundationModelAgreementOffers"];
            }
            var message = offer is not null
                ? $"{shown} is sold through AWS Marketplace. Review its terms and prices, then accept them to subscribe — usage is billed to your AWS account."
                : $"{shown} is sold through AWS Marketplace. AWS subscribes your account the first time the model is used; that can take up to 15 minutes.";
            return new ModelAccess(ModelAccessState.NeedsAgreement, modelId, message, offer, missing, useCase, availability);
        }
        if (availability.Agreement.Equals("PENDING", StringComparison.OrdinalIgnoreCase)
            || availability.Entitlement.Equals("NOT_AVAILABLE", StringComparison.OrdinalIgnoreCase))
            return new ModelAccess(ModelAccessState.Pending, modelId,
                $"AWS is setting up your access to {shown}. This can take up to 15 minutes.", UseCaseSubmitted: useCase, Availability: availability);
        return new ModelAccess(ModelAccessState.Ready, modelId, $"{shown} is ready to use.", UseCaseSubmitted: useCase, Availability: availability);
    }

    /// <summary>Read GetFoundationModelAvailability's answer.</summary>
    public static BedrockAvailability ParseAvailability(JsonNode? json)
    {
        var obj = json as JsonObject ?? new JsonObject();
        var agreement = obj["agreementAvailability"] as JsonObject ?? new JsonObject();
        return new BedrockAvailability(
            JsonArgs.String(agreement, "status") ?? "",
            JsonArgs.String(agreement, "errorMessage"),
            JsonArgs.String(obj, "authorizationStatus") ?? "",
            JsonArgs.String(obj, "entitlementAvailability") ?? "",
            JsonArgs.String(obj, "regionAvailability") ?? "");
    }

    /// <summary>The first offer for a Marketplace model, or null when AWS lists none.</summary>
    public async Task<ModelOffer?> FirstOfferAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var json = await JsonAsync(["bedrock", "list-foundation-model-agreement-offers", "--model-id", modelId], cancellationToken).ConfigureAwait(false);
        return ParseOffers(json).FirstOrDefault();
    }

    /// <summary>Read ListFoundationModelAgreementOffers' answer.</summary>
    public static IReadOnlyList<ModelOffer> ParseOffers(JsonNode? json)
    {
        var offers = new List<ModelOffer>();
        foreach (var node in json?["offers"] as JsonArray ?? [])
        {
            if (node is not JsonObject offer || JsonArgs.String(offer, "offerToken") is not { Length: > 0 } token) continue;
            var terms = offer["termDetails"] as JsonObject ?? new JsonObject();
            var prices = new List<ModelPrice>();
            foreach (var rate in terms["usageBasedPricingTerm"]?["rateCard"] as JsonArray ?? [])
            {
                if (rate is not JsonObject r) continue;
                prices.Add(new ModelPrice(JsonArgs.String(r, "dimension") ?? "", JsonArgs.String(r, "price") ?? "?",
                    JsonArgs.String(r, "unit"), JsonArgs.String(r, "description")));
            }
            offers.Add(new ModelOffer(token, JsonArgs.String(offer, "offerId"),
                terms["legalTerm"] is JsonObject legal ? JsonArgs.String(legal, "url") : null,
                prices,
                terms["supportTerm"] is JsonObject support ? JsonArgs.String(support, "refundPolicyDescription") : null,
                terms["validityTerm"] is JsonObject validity ? JsonArgs.String(validity, "agreementDuration") : null));
        }
        return offers;
    }

    /// <summary>"P1Y" → "1 year", "P12M" → "12 months", "P30D" → "30 days"; anything else unchanged.</summary>
    public static string FriendlyDuration(string duration)
    {
        var match = DurationRegex().Match(duration.Trim());
        if (!match.Success) return duration;
        var parts = new List<string>();
        void Add(string group, string unit)
        {
            if (match.Groups[group].Success && int.TryParse(match.Groups[group].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0)
                parts.Add(n == 1 ? $"1 {unit}" : $"{n} {unit}s");
        }
        Add("y", "year");
        Add("m", "month");
        Add("w", "week");
        Add("d", "day");
        return parts.Count == 0 ? duration : string.Join(", ", parts);
    }

    [GeneratedRegex(@"^P(?:(?<y>\d+)Y)?(?:(?<m>\d+)M)?(?:(?<w>\d+)W)?(?:(?<d>\d+)D)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();

    /// <summary>Whether Anthropic's use-case form is on file for this account: true, false (AWS says
    /// there is none), or null when DSH can't tell (for example, no permission to read it).</summary>
    public async Task<bool?> UseCaseOnFileAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await JsonAsync(["bedrock", "get-use-case-for-model-access"], cancellationToken).ConfigureAwait(false);
            return json is JsonObject obj && JsonArgs.String(obj, "formData") is { Length: > 0 };
        }
        catch (AwsCliException ex) when (ex.Kind == AwsCliFailure.NotFound
                                         || ex.Error.Details.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        catch (AwsCliException)
        {
            return null;
        }
    }

    /// <summary>The form on file, or null when there is none (or it can't be read).</summary>
    public async Task<UseCaseForm?> GetUseCaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await JsonAsync(["bedrock", "get-use-case-for-model-access"], cancellationToken).ConfigureAwait(false);
            return json is JsonObject obj && JsonArgs.String(obj, "formData") is { Length: > 0 } data ? UseCaseForm.FromFormData(data) : null;
        }
        catch (AwsCliException)
        {
            return null;
        }
    }

    /// <summary>The CLI arguments that submit <paramref name="form"/>.</summary>
    public static IReadOnlyList<string> SubmitUseCaseCommand(UseCaseForm form) =>
        ["bedrock", "put-use-case-for-model-access", "--form-data", form.ToBase64(), "--cli-binary-format", "base64"];

    /// <summary>Submit Anthropic's use-case form, unless this account already has one (it can't be
    /// changed, so it isn't sent twice). Throws <see cref="ArgumentException"/> with a plain message when
    /// the form is incomplete.</summary>
    public async Task<UseCaseSubmission> SubmitUseCaseAsync(UseCaseForm form, CancellationToken cancellationToken = default)
    {
        if (form.Problem is { } problem) throw new ArgumentException(problem, nameof(form));
        if (await UseCaseOnFileAsync(cancellationToken).ConfigureAwait(false) == true) return UseCaseSubmission.AlreadyOnFile;
        await _cli.RunCheckedAsync(SubmitUseCaseCommand(form), Profile, Region, cancellationToken: cancellationToken).ConfigureAwait(false);
        return UseCaseSubmission.Submitted;
    }

    /// <summary>Accept a Marketplace offer's terms (CreateFoundationModelAgreement). AWS then subscribes
    /// the account; follow with <see cref="WaitUntilReadyAsync"/>. An agreement already in progress
    /// (ConflictException) counts as accepted.</summary>
    public async Task AcceptAgreementAsync(string modelId, string offerToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(offerToken);
        try
        {
            await _cli.RunCheckedAsync(["bedrock", "create-foundation-model-agreement", "--model-id", modelId, "--offer-token", offerToken],
                Profile, Region, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (AwsCliException ex) when (ex.Kind == AwsCliFailure.Conflict)
        {
            // Already accepted, or being processed.
        }
    }

    /// <summary>Check access now and then every <paramref name="pollInterval"/> (default 15 s) until the
    /// model is ready, needs something from the user, or <paramref name="timeout"/> (default 15 minutes)
    /// passes; each check is reported. "Pending" and "needs agreement" keep it waiting — right after an
    /// agreement is accepted AWS can still report it as not available for a moment. Returns the last check.</summary>
    public async Task<ModelAccess> WaitUntilReadyAsync(BedrockModel model, IProgress<ModelAccess>? progress = null, TimeSpan? timeout = null,
                                                       TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        var deadline = _time.GetUtcNow() + (timeout ?? TimeSpan.FromMinutes(15));
        var interval = pollInterval ?? TimeSpan.FromSeconds(15);
        while (true)
        {
            var access = await CheckAccessAsync(model, cancellationToken).ConfigureAwait(false);
            progress?.Report(access);
            if (access.State is not (ModelAccessState.Pending or ModelAccessState.NeedsAgreement)) return access;
            var left = deadline - _time.GetUtcNow();
            if (left <= TimeSpan.Zero) return access;
            await Task.Delay(left < interval ? left : interval, _time, cancellationToken).ConfigureAwait(false);
        }
    }
}

// MARK: - Permissions

/// <summary>The IAM permissions DSH's Bedrock setup and chat use, as a policy an AWS administrator can
/// attach to the person (or their role).</summary>
public static class BedrockPermissions
{
    /// <summary>Calling a model: Converse is authorized by InvokeModel, ConverseStream by
    /// InvokeModelWithResponseStream (there are no separate Converse actions).</summary>
    public static IReadOnlyList<string> InvokeActions { get; } = ["bedrock:InvokeModel", "bedrock:InvokeModelWithResponseStream"];

    /// <summary>The AWS managed policy that lets an identity use `aws login`.</summary>
    public const string SignInManagedPolicy = "SignInLocalDevelopmentAccess";

    /// <summary>Least privilege for DSH: call models (directly or through cross-Region inference profiles,
    /// whose underlying models live in other Regions — and, for global profiles, in no Region, which the
    /// empty-matching "*" covers); list models and check access; accept Marketplace terms and the Anthropic
    /// form; Bedrock API keys; let Bedrock (and only Bedrock, aws:CalledViaLast) subscribe to Marketplace
    /// models; and `aws login`'s own two actions. An administrator can narrow the Marketplace statement
    /// further with aws-marketplace:ProductId.</summary>
    public const string PolicyJson = """
        {
          "Version": "2012-10-17",
          "Statement": [
            {
              "Sid": "DshCallBedrockModels",
              "Effect": "Allow",
              "Action": [
                "bedrock:InvokeModel",
                "bedrock:InvokeModelWithResponseStream"
              ],
              "Resource": [
                "arn:aws:bedrock:*::foundation-model/*",
                "arn:aws:bedrock:*:*:inference-profile/*"
              ]
            },
            {
              "Sid": "DshSetUpModelAccess",
              "Effect": "Allow",
              "Action": [
                "bedrock:ListFoundationModels",
                "bedrock:GetFoundationModel",
                "bedrock:GetFoundationModelAvailability",
                "bedrock:ListInferenceProfiles",
                "bedrock:GetInferenceProfile",
                "bedrock:ListFoundationModelAgreementOffers",
                "bedrock:CreateFoundationModelAgreement",
                "bedrock:GetUseCaseForModelAccess",
                "bedrock:PutUseCaseForModelAccess"
              ],
              "Resource": "*"
            },
            {
              "Sid": "DshBedrockApiKeys",
              "Effect": "Allow",
              "Action": "bedrock:CallWithBearerToken",
              "Resource": "*"
            },
            {
              "Sid": "DshMarketplaceSubscriptionsThroughBedrock",
              "Effect": "Allow",
              "Action": [
                "aws-marketplace:Subscribe",
                "aws-marketplace:ViewSubscriptions",
                "aws-marketplace:Unsubscribe"
              ],
              "Resource": "*",
              "Condition": {
                "StringEquals": {
                  "aws:CalledViaLast": "bedrock.amazonaws.com"
                }
              }
            },
            {
              "Sid": "DshSignInWithAwsLogin",
              "Effect": "Allow",
              "Action": [
                "signin:AuthorizeOAuth2Access",
                "signin:CreateOAuth2Token"
              ],
              "Resource": "arn:aws:signin:*:*:oauth2/public-client/*"
            }
          ]
        }
        """;

    /// <summary>What to tell the person who has to add the policy.</summary>
    public const string Explanation =
        "DSH uses Amazon Bedrock with your own AWS sign-in. If AWS says you aren't allowed to do something, " +
        "send this policy to whoever manages your AWS account and ask them to attach it to you (or to the role you sign in with). " +
        "It lets DSH: call Bedrock models, including through cross-Region inference profiles; list the models in your Region and check " +
        "whether your account can use them; accept a model's terms and Anthropic's one-time use-case form when you choose to; " +
        "use a Bedrock API key; let Bedrock subscribe to AWS Marketplace models on your behalf (only through Bedrock); and sign in " +
        "from the AWS CLI with \"aws login\" (the same as the AWS managed policy SignInLocalDevelopmentAccess). " +
        "It doesn't allow creating, changing or deleting anything else in the account.";
}
