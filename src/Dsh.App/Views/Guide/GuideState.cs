using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dsh.Core;

namespace Dsh.App.Views.Guide;

/// <summary>How the user's Spark starts out: just unboxed, to be reinstalled, or already set up.</summary>
public enum GuideStart { Unset, New, Fresh, Existing }

/// <summary>The pages of the DGX Spark guide, in the order a brand-new Spark walks through them.</summary>
public enum GuidePage
{
    Welcome,
    Start,
    Download,
    WriteUsb,
    BootUsb,
    Cables,
    Sticker,
    Hotspot,
    SetupPage,
    Wait,
    Find,
    Install,
    Certificate,
    Admin,
    Model,
    Connect,
    Done,
}

/// <summary>Where the user is in the guide and what they have done, saved after every step so
/// closing the window (or the app) and coming back picks up where they left off. It never holds a
/// password: the Spark's login is asked for again, and the Swapper's lives in Credential Manager.</summary>
public sealed class GuideState
{
    public GuidePage Page { get; set; } = GuidePage.Welcome;
    public GuideStart Start { get; set; }
    /// <summary>The Spark's address (IP or name) as the guide reaches it.</summary>
    public string? Host { get; set; }
    public string? HostName { get; set; }
    public string? SshUser { get; set; }
    public bool SwapperInstalled { get; set; }
    /// <summary>The Swapper's certificate the user trusted (pinned).</summary>
    public string? SwapperFingerprint { get; set; }
    /// <summary>The certificate of the HTTPS model front on :11443, when it differs.</summary>
    public string? FrontFingerprint { get; set; }
    public bool AdminReady { get; set; }
    public string? ModelKey { get; set; }
    public bool Connected { get; set; }
    public string? RecoverySource { get; set; }
    public bool Completed { get; set; }
    public DateTimeOffset Updated { get; set; }

    [JsonIgnore] public bool HasProgress => !Completed && Page != GuidePage.Welcome;

    public static string FilePath => System.IO.Path.Combine(AppPaths.Root, "spark-guide.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static GuideState? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<GuideState>(File.ReadAllText(FilePath), Json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save()
    {
        Updated = DateTimeOffset.Now;
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, Json), TextUtil.Utf8NoBom);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Progress is a convenience; the guide works without it.
        }
    }

    public static void Clear()
    {
        try { File.Delete(FilePath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
