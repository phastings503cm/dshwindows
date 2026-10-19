namespace Vision.Core;

// MARK: - What a route is sent for each attachment

/// <summary>What a route takes besides text.</summary>
public sealed record AttachmentRoute
{
    /// <summary>Images (image_url parts, Bedrock image blocks).</summary>
    public bool Vision { get; init; }
    /// <summary>Native PDF documents (Bedrock document blocks).</summary>
    public bool Documents { get; init; }
    /// <summary>Image formats the route takes, by MIME type; null = any.</summary>
    public IReadOnlySet<string>? ImageTypes { get; init; }
    /// <summary>Per-message limits (Bedrock: 20 images of at most 3.75 MB, 5 documents of at most 4.5 MB).</summary>
    public int? MaxImages { get; init; }
    public long? MaxImageBytes { get; init; }
    public int? MaxDocuments { get; init; }
    public long? MaxDocumentBytes { get; init; }

    public static AttachmentRoute TextOnly { get; } = new() { Vision = false };

    /// <summary>An OpenAI-compatible route (self-hosted servers, hosted APIs, Guardian): images when the model takes them
    /// (the route's setting, unless its server already turned one down), never a file part.</summary>
    public static AttachmentRoute OpenAi(ProviderProfile profile, string? model = null)
    {
        // Unset: what the model is known to take (the same rule as the chat's vision setting). An API that takes text only
        // (Z.ai's text models refuse content parts) never gets a picture, whatever the route says.
        var rules = ModelRequestRules.For(profile, model);
        var vision = !rules.TextOnlyContent && (profile.Vision ?? rules.AcceptsImages ?? true);
        return new() { Vision = vision && !VisionRejections.Shared.Rejected(VisionRejections.Key(profile, model)) };
    }

    /// <summary>A Bedrock route: Converse's image formats and limits, and PDF documents when the model reads them.</summary>
    public static AttachmentRoute Bedrock(ProviderProfile profile, BedrockModelTraits traits) => new()
    {
        Vision = profile.Vision ?? true,
        Documents = traits.Documents,
        ImageTypes = new HashSet<string>(StringComparer.Ordinal) { "image/png", "image/jpeg", "image/gif", "image/webp" },
        MaxImages = 20,
        MaxImageBytes = 3_750_000,
        MaxDocuments = 5,
        MaxDocumentBytes = 4_500_000,
    };

    /// <summary>Anthropic's Messages API: base64 images in its four formats, and PDFs as document blocks. Limits kept under
    /// the API's (5 MB an image, 32 MB a request once base64 has grown it by a third).</summary>
    public static AttachmentRoute Anthropic(ProviderProfile profile) => new()
    {
        Vision = profile.Vision ?? true,
        Documents = BedrockModels.Traits(profile.Model).Documents,
        ImageTypes = new HashSet<string>(StringComparer.Ordinal) { "image/png", "image/jpeg", "image/gif", "image/webp" },
        MaxImages = 20,
        MaxImageBytes = 3_750_000,
        MaxDocuments = 5,
        MaxDocumentBytes = 10_000_000,
    };

    /// <summary>The route a profile is served on.</summary>
    public static AttachmentRoute Of(ProviderProfile profile) => profile.Kind switch
    {
        ProviderKind.Bedrock => Bedrock(profile, BedrockModels.Traits(profile.Model)),
        ProviderKind.Anthropic => Anthropic(profile),
        _ => OpenAi(profile),
    };
}

/// <summary>How one attachment goes to the model — what the composer chip says after the file's summary.</summary>
public abstract record AttachmentDelivery
{
    public sealed record Image : AttachmentDelivery;
    public sealed record OcrText : AttachmentDelivery;
    public sealed record Document : AttachmentDelivery;
    public sealed record PageImages(int Count) : AttachmentDelivery;
    public sealed record Text : AttachmentDelivery;
    public sealed record TextInParts(int Count) : AttachmentDelivery;
    public sealed record NameOnly : AttachmentDelivery;
}

public static class AttachmentDeliveries
{
    /// <summary>"sent as text", "sent as text in 13 parts", … (pinned wording, the same on macOS).</summary>
    public static string Label(this AttachmentDelivery delivery) => delivery switch
    {
        AttachmentDelivery.Image => "sent as image",
        AttachmentDelivery.OcrText => "sent as text (OCR)",
        AttachmentDelivery.Document => "sent as PDF document",
        AttachmentDelivery.PageImages { Count: 1 } => "sent as 1 page image",
        AttachmentDelivery.PageImages p => $"sent as {AttachmentFormat.Grouped(p.Count)} page images",
        AttachmentDelivery.Text => "sent as text",
        AttachmentDelivery.TextInParts p => $"sent as text in {AttachmentFormat.Grouped(p.Count)} parts",
        _ => "can't be read — name only",
    };
}

/// <summary>One piece of a user message on the wire.</summary>
public abstract record AttachmentWirePart
{
    public sealed record Text(string Value) : AttachmentWirePart;
    public sealed record Image(string Mime, byte[] Data) : AttachmentWirePart;
    public sealed record Document(string Name, byte[] Data) : AttachmentWirePart;
}

public static class AttachmentWire
{
    /// <summary>How <paramref name="attachment"/> goes on <paramref name="route"/>. <paramref name="parts"/> overrides the
    /// attachment's own count (the composer, before the message is prepared).</summary>
    public static AttachmentDelivery Delivery(MessageAttachment attachment, AttachmentRoute route, int? parts = null)
    {
        if (attachment.Kind == AttachmentKind.Image)
        {
            return route.Vision && (route.ImageTypes?.Contains(attachment.Mime) ?? true)
                ? new AttachmentDelivery.Image()
                : new AttachmentDelivery.OcrText();
        }
        if (attachment.Digest is not { } digest)
        {
            // Read by nobody (an older caller): a PDF goes as a document where the route takes one, text as text.
            if (attachment.Mime == "application/pdf" && route.Documents) return new AttachmentDelivery.Document();
            return AttachmentReader.DecodeText(attachment.Data, lenient: false) is null ? new AttachmentDelivery.NameOnly() : new AttachmentDelivery.Text();
        }
        var count = parts ?? attachment.Parts ?? 1;
        if (digest.Format == DigestFormat.Pdf && digest.Problem is null)
        {
            if (digest.Scanned && route.Vision && digest.PageImages.Count > 0) return new AttachmentDelivery.PageImages(digest.PageImages.Count);
            if (route.Documents && !digest.Scanned && count == 1 && attachment.Data.Length > 0 &&
                attachment.Data.LongLength <= (route.MaxDocumentBytes ?? long.MaxValue))
                return new AttachmentDelivery.Document();
        }
        if (!digest.Readable) return new AttachmentDelivery.NameOnly();
        return count > 1 ? new AttachmentDelivery.TextInParts(count) : new AttachmentDelivery.Text();
    }

    /// <summary>The wire pieces for a user message's attachments, in order, within the route's per-message limits (past
    /// them, a picture or document goes as its text instead).</summary>
    public static IReadOnlyList<AttachmentWirePart> Parts(IReadOnlyList<MessageAttachment> attachments, AttachmentRoute route)
    {
        var output = new List<AttachmentWirePart>();
        var images = 0;
        var documents = 0;
        bool ImageFits(byte[] data) => images < (route.MaxImages ?? int.MaxValue) && data.LongLength <= (route.MaxImageBytes ?? long.MaxValue);
        foreach (var attachment in attachments)
        {
            switch (Delivery(attachment, route))
            {
                case AttachmentDelivery.Image:
                    if (ImageFits(attachment.Data))
                    {
                        output.Add(new AttachmentWirePart.Image(attachment.Mime, attachment.Data));
                        images++;
                    }
                    else
                    {
                        output.Add(new AttachmentWirePart.Text(FallbackText(attachment)));
                    }
                    break;
                case AttachmentDelivery.Document:
                    if (documents < (route.MaxDocuments ?? int.MaxValue))
                    {
                        output.Add(new AttachmentWirePart.Document(attachment.Name, attachment.Data));
                        documents++;
                    }
                    else
                    {
                        output.Add(new AttachmentWirePart.Text(FallbackText(attachment)));
                    }
                    break;
                case AttachmentDelivery.PageImages:
                {
                    var digest = attachment.Digest!;
                    var room = Math.Max(0, (route.MaxImages ?? int.MaxValue) - images);
                    var pages = digest.PageImages.Take(room)
                        .Where(p => p.Data.LongLength <= (route.MaxImageBytes ?? long.MaxValue)
                                    && (route.ImageTypes?.Contains(p.Mime) ?? true)).ToList();
                    if (pages.Count == 0)
                    {
                        output.Add(new AttachmentWirePart.Text(FallbackText(attachment)));
                        break;
                    }
                    var total = digest.Pages ?? pages.Count;
                    var head = $"[Attached file: {attachment.Name} — {digest.Summary} — it has no text layer, so " +
                               (pages.Count == total ? "its pages follow as images]" : $"its first {pages.Count} of {total} pages follow as images]");
                    output.Add(new AttachmentWirePart.Text(head));
                    foreach (var page in pages) output.Add(new AttachmentWirePart.Image(page.Mime, page.Data));
                    images += pages.Count;
                    // Pages past the images: what OCR read from the whole file.
                    if (pages.Count < total && digest.Ocr) output.Add(new AttachmentWirePart.Text(FallbackText(attachment)));
                    break;
                }
                default:
                    output.Add(new AttachmentWirePart.Text(FallbackText(attachment)));
                    break;
            }
        }
        return output;
    }

    /// <summary>The text sent in an attachment's place: what preparing it produced, else what can be made of it now.</summary>
    public static string FallbackText(MessageAttachment attachment)
    {
        if (attachment.Text is { } text) return text;
        if (attachment.Digest is { } digest)
        {
            if (attachment.Kind == AttachmentKind.Image) return AttachmentReader.ImageText(attachment.Name, digest.Text);
            return AttachmentPrep.InlineText(attachment.Name, digest, new AttachmentBudget(1_000_000), storedName: null).Text;
        }
        // Nobody read it: its bytes when they are text, else a note.
        if (AttachmentReader.DecodeText(attachment.Data, lenient: false) is { } decoded) return $"[Attached file: {attachment.Name}]\n{decoded}";
        return $"[Attached file: {attachment.Name} ({attachment.Data.Length} bytes of binary data, not shown)]";
    }

    /// <summary>Whether any of these messages carries an image (a picture, a scanned PDF's pages) that would go as one on
    /// <paramref name="route"/>.</summary>
    public static bool CarriesImages(IEnumerable<LlmMessage> messages, AttachmentRoute route) =>
        messages.Any(m => m.Role == MessageRole.User && m.Attachments is { Count: > 0 } attachments
                          && Parts(attachments, route).Any(p => p is AttachmentWirePart.Image));
}

// MARK: - Servers that turned down an image

/// <summary>Routes (base URL + model) whose server answered an image with an error saying it takes none — learned once,
/// so later requests send the picture's OCR text instead of failing again. Pinned bodies: llama.cpp "image input is not
/// supported - hint: if this is unexpected, you may need to provide the mmproj"; vLLM "Resolving modality {'image'}
/// requires a multimodal processor but none is available." and (older) "At most 0 image(s) may be provided in one
/// prompt."</summary>
public sealed class VisionRejections
{
    public static VisionRejections Shared { get; } = new();

    private readonly Lock _lock = new();
    private readonly HashSet<string> _routes = new(StringComparer.Ordinal);

    public static string Key(ProviderProfile profile, string? model = null) =>
        $"{profile.BaseUrl}|{(string.IsNullOrEmpty(model) ? profile.Model : model)}";

    public bool Rejected(string key)
    {
        lock (_lock) return _routes.Contains(key);
    }

    public void Learn(string key)
    {
        lock (_lock) _routes.Add(key);
    }

    public void Reset()
    {
        lock (_lock) _routes.Clear();
    }

    private static readonly string[] Phrases =
    [
        "image input is not supported", "requires a multimodal processor", "at most 0 image",
        "does not support image", "doesn't support image", "do not support image", "not support images",
        "images are not supported", "image inputs are not supported", "is not a multimodal model",
        "does not support vision", "vision is not supported", "multimodal input is not supported",
        "model does not support multimodal",
    ];

    /// <summary>Whether an error body says the model takes no images.</summary>
    public static bool SaysNoImages(string body)
    {
        var lower = body.ToLowerInvariant();
        return Phrases.Any(p => lower.Contains(p, StringComparison.Ordinal));
    }
}
