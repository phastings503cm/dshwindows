using CommunityToolkit.Mvvm.ComponentModel;
using Vision.App.Infrastructure;
using Vision.Core;

namespace Vision.App.Views.Chat;

/// <summary>One attachment waiting in the composer, as its chip shows it: "report.pdf  PDF · 42 pages · sent as text",
/// or for a large paste "Pasted text · 10,214 lines · 412 KB · sent as text in 13 parts" (view, edit, remove).</summary>
public sealed partial class ComposerChip : ObservableObject
{
    public ComposerChip(MessageAttachment attachment) => _attachment = attachment;

    /// <summary>The attachment, read (its digest filled in) once reading finishes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(IsReading), nameof(IsPasted), nameof(ShowsName), nameof(Summary), nameof(Label),
        nameof(Glyph), nameof(Tooltip))]
    private MessageAttachment _attachment;

    /// <summary>How it will be sent on the current route ("sent as text", "sent as image" …); empty while it is read.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Label), nameof(Tooltip))] private string _delivery = "";

    public string Name => Attachment.Name;

    /// <summary>A file Vision is still reading (a big PDF can take a moment).</summary>
    public bool IsReading => Attachment.Digest is null && Attachment.Kind == AttachmentKind.File;

    public bool IsPasted => Attachment.Digest?.Format == DigestFormat.Pasted;

    /// <summary>A paste's file name means nothing to the user; the chip names it by its summary alone.</summary>
    public bool ShowsName => !IsPasted;

    public string Summary => Attachment.Digest?.Summary ?? (Attachment.Kind == AttachmentKind.Image ? "Image" : "Reading…");

    public string Label => Delivery.Length > 0 && !IsReading ? $"{Summary} · {Delivery}" : Summary;

    public string Glyph => IsPasted ? Icons.Document : Attachment.Kind == AttachmentKind.Image ? Icons.Picture : Icons.Attach;

    public string Tooltip
    {
        get
        {
            var lines = new List<string> { IsPasted ? "Pasted text" : Name, Label };
            if (Attachment.Digest?.Problem is { } problem) lines.Add(problem);
            if (IsPasted) lines.Add("Click to view or edit it.");
            return string.Join("\n", lines);
        }
    }

    // The part count for the chip's label, kept per part size (splitting a long text costs a pass over it).
    private (int Inline, int Part) _partsFor = (-1, -1);
    private int _parts = 1;
    private AttachmentDigest? _partsDigest;

    /// <summary>How many parts its text makes under <paramref name="budget"/>.</summary>
    public int PartCount(AttachmentBudget budget)
    {
        if (Attachment.Digest is not { } digest) return 1;
        if (_partsFor != (budget.InlineChars, budget.PartChars) || !ReferenceEquals(_partsDigest, digest))
        {
            _parts = AttachmentPrep.PartCount(digest, budget);
            _partsFor = (budget.InlineChars, budget.PartChars);
            _partsDigest = digest;
        }
        return _parts;
    }
}
