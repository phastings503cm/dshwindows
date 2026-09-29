using System.ComponentModel;
using System.Diagnostics;
using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Dsh.Core;

// MARK: - DGX Spark recovery USB
//
// Reinstalling a DGX Spark ("System Recovery") boots it from a USB stick made from NVIDIA's recovery
// media archive. NVIDIA's own Windows script often fails ("Set-Disk: Not Supported"), so this writes
// the layout that works: MBR, one primary partition of at most 31 GB (the largest Windows will
// format as FAT32), marked active, FAT32, volume label BOOTME, the archive's files copied on, and
// EFI\BOOT\recovery.txt containing the word RECOVERY.
//
// Safety first. Only USB disks are offered — never the disk Windows runs from, nothing over 256 GB —
// the user confirms a sentence that names the drive, and the disk is checked again (same number,
// serial and size, still USB, still not holding Windows) right before it is erased. Erasing needs
// administrator rights, so the app starts a copy of itself elevated, just for this job
// (DSH.exe --write-recovery-usb <job.json>), and the main app stays unelevated; the helper reports
// progress through a small status file.

/// <summary>A physical disk, as Windows' storage stack describes it.</summary>
public sealed record UsbDisk
{
    public int Number { get; init; }
    public string FriendlyName { get; init; } = "";
    public long Size { get; init; }
    public string? SerialNumber { get; init; }
    /// <summary>"USB", "NVMe", "SATA", ... (MSFT_Disk.BusType).</summary>
    public string BusType { get; init; } = "";
    public bool IsBoot { get; init; }
    public bool IsSystem { get; init; }
    public bool IsOffline { get; init; }
    public bool IsReadOnly { get; init; }
    public string? PartitionStyle { get; init; }
    public IReadOnlyList<char> DriveLetters { get; init; } = [];
    public IReadOnlyList<string> Labels { get; init; } = [];

    public bool IsUsb => BusType.Equals("USB", StringComparison.OrdinalIgnoreCase);

    /// <summary>"32 GB" — decimal gigabytes, like the number printed on the stick.</summary>
    public string SizeText => RecoveryUsb.FormatSize(Size);

    /// <summary>"SanDisk Ultra 32 GB (E:)".</summary>
    public string Describe()
    {
        var letters = DriveLetters.Count == 0 ? "" : $" ({string.Join(", ", DriveLetters.Select(l => $"{l}:"))})";
        return $"{FriendlyName.Trim()} {SizeText}{letters}";
    }
}

public enum RecoverySourceKind { TarGz, Tar, Zip, Folder }

/// <summary>What is inside the recovery archive the user picked.</summary>
public sealed record RecoverySource(
    string Path, RecoverySourceKind Kind, string Root, long TotalBytes, int FileCount, long LargestFile, string? LargestName, bool HasEfi)
{
    /// <summary>Why this can't go on a FAT32 stick of <paramref name="capacity"/> bytes, or null.</summary>
    public string? Problem(long capacity)
    {
        if (FileCount == 0) return "That file is empty — it doesn't look like the recovery media.";
        if (LargestFile >= RecoveryUsb.Fat32MaxFile)
            return $"{LargestName} is {RecoveryUsb.FormatSize(LargestFile)}, larger than the 4 GB a FAT32 stick can hold in one file.";
        if (TotalBytes > capacity - 64L * 1024 * 1024)
            return $"The recovery files need {RecoveryUsb.FormatSize(TotalBytes)}, more than this stick can hold ({RecoveryUsb.FormatSize(capacity)}).";
        return null;
    }
}

public sealed record RecoveryProgress(string Stage, long Done, long Total, string? CurrentFile = null)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
}

public static class RecoveryUsb
{
    /// <summary>The smallest and largest disks offered (sticks sold as 8 and 256 GB included).</summary>
    public const long MinBytes = 7_500_000_000;
    public const long MaxBytes = 264_000_000_000;
    /// <summary>NVIDIA asks for 16 GB or larger.</summary>
    public const long RecommendedBytes = 14_500_000_000;
    /// <summary>The largest partition Windows formats as FAT32 (diskpart refuses above 32 GB).</summary>
    public const long MaxPartitionMb = 31 * 1024;
    /// <summary>FAT32 stores files up to 4 GiB − 1 byte.</summary>
    public const long Fat32MaxFile = 4L * 1024 * 1024 * 1024;
    public const string VolumeLabel = "BOOTME";

    public static string FormatSize(long bytes) => bytes >= 1_000_000_000
        ? (bytes / 1e9).ToString(bytes >= 100_000_000_000 ? "0" : "0.#", CultureInfo.InvariantCulture) + " GB"
        : (bytes / 1e6).ToString("0", CultureInfo.InvariantCulture) + " MB";

    // MARK: - Which disks

    /// <summary>Why this disk is not offered, in plain words; null when it may be used.</summary>
    public static string? Ineligible(UsbDisk disk, char? systemDrive = 'C')
    {
        if (!disk.IsUsb) return "It isn't a USB drive.";
        if (disk.IsBoot || disk.IsSystem) return "Windows starts from this drive.";
        if (systemDrive is { } c && disk.DriveLetters.Any(l => char.ToUpperInvariant(l) == char.ToUpperInvariant(c)))
            return $"It holds Windows ({char.ToUpperInvariant(c)}:).";
        if (disk.IsReadOnly) return "It is write-protected (check for a lock switch on the stick).";
        if (disk.IsOffline) return "Windows has it switched off (offline in Disk Management).";
        if (disk.Size < MinBytes) return $"It is too small ({disk.SizeText}); use a 16 GB or larger stick.";
        if (disk.Size > MaxBytes) return $"It is larger than 256 GB ({disk.SizeText}), so it's probably not a USB stick — DSH won't erase it.";
        return null;
    }

    public static IReadOnlyList<UsbDisk> Eligible(IEnumerable<UsbDisk> disks, char? systemDrive = 'C') =>
        disks.Where(d => Ineligible(d, systemDrive) is null).OrderBy(d => d.Number).ToList();

    /// <summary>Read the JSON the disk listing script prints (one object, or an array of them).</summary>
    public static IReadOnlyList<UsbDisk> ParseDisks(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }
        var items = root switch
        {
            JsonArray array => array.OfType<JsonObject>(),
            JsonObject single => [single],
            _ => [],
        };
        return items.Select(ParseDisk).ToList();
    }

    private static UsbDisk ParseDisk(JsonObject obj)
    {
        static IEnumerable<string> Strings(JsonNode? node) => node switch
        {
            JsonArray array => array.Select(n => n?.ToString() ?? ""),
            JsonValue value => [value.ToString()],
            _ => [],
        };
        var bus = obj["BusType"]?.ToString() ?? "";
        // MSFT_Disk.BusType is a number in raw CIM output (7 = USB); Get-Disk shows the name.
        if (int.TryParse(bus, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            bus = code switch { 7 => "USB", 11 => "SATA", 17 => "NVMe", 8 => "RAID", 12 => "SD", 14 or 15 => "Virtual", _ => code.ToString(CultureInfo.InvariantCulture) };
        return new UsbDisk
        {
            Number = JsonArgs.Int(obj, "Number", -1),
            FriendlyName = JsonArgs.String(obj, "FriendlyName") ?? "USB drive",
            Size = obj["Size"] is JsonValue size && size.TryGetValue<long>(out var bytes) ? bytes : 0,
            SerialNumber = (JsonArgs.String(obj, "SerialNumber") ?? "").Trim() is { Length: > 0 } serial ? serial : null,
            BusType = bus,
            IsBoot = JsonArgs.Bool(obj, "IsBoot", false),
            IsSystem = JsonArgs.Bool(obj, "IsSystem", false),
            IsOffline = JsonArgs.Bool(obj, "IsOffline", false),
            IsReadOnly = JsonArgs.Bool(obj, "IsReadOnly", false),
            PartitionStyle = JsonArgs.String(obj, "PartitionStyle"),
            DriveLetters = Strings(obj["DriveLetters"])
                .Select(s => s.Trim()).Where(s => s.Length == 1 && char.IsAsciiLetter(s[0]))
                .Select(s => char.ToUpperInvariant(s[0])).ToList(),
            Labels = Strings(obj["Labels"]).Where(s => s.Trim().Length > 0).ToList(),
        };
    }

    /// <summary>Lists every disk with its partitions' drive letters and volume labels. Read-only;
    /// needs no administrator rights.</summary>
    public const string ListDisksScript = """
        $ErrorActionPreference = 'SilentlyContinue'
        $disks = @(Get-Disk | ForEach-Object {
          $d = $_
          $parts = @(Get-Partition -DiskNumber $d.Number)
          [pscustomobject]@{
            Number = [int]$d.Number
            FriendlyName = [string]$d.FriendlyName
            Size = [int64]$d.Size
            SerialNumber = ([string]$d.SerialNumber).Trim()
            BusType = [string]$d.BusType
            IsBoot = [bool]$d.IsBoot
            IsSystem = [bool]$d.IsSystem
            IsOffline = [bool]$d.IsOffline
            IsReadOnly = [bool]$d.IsReadOnly
            PartitionStyle = [string]$d.PartitionStyle
            DriveLetters = @($parts | ForEach-Object { [string]$_.DriveLetter } | Where-Object { $_ -match '^[A-Za-z]$' })
            Labels = @($parts | Get-Volume | ForEach-Object { [string]$_.FileSystemLabel } | Where-Object { $_ })
          }
        })
        ConvertTo-Json -InputObject $disks -Depth 3 -Compress
        """;

    /// <summary>The disks Windows sees (all of them; filter with <see cref="Eligible"/>).</summary>
    [SupportedOSPlatform("windows")]
    public static async Task<IReadOnlyList<UsbDisk>> ListDisksAsync(CancellationToken cancellationToken = default)
    {
        var (exit, output, error) = await PowerShellAsync(ListDisksScript, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        if (exit != 0 && output.Trim().Length == 0) throw new IOException($"Couldn't list the drives ({error.Trim()}).");
        return ParseDisks(output);
    }

    public static char? SystemDriveLetter()
    {
        var drive = Environment.GetEnvironmentVariable("SystemDrive");
        return drive is { Length: >= 1 } && char.IsAsciiLetter(drive[0]) ? char.ToUpperInvariant(drive[0]) : null;
    }

    // MARK: - diskpart

    /// <summary>The partition size in MB, or null to use the whole disk (it already fits FAT32).</summary>
    public static long? PartitionSizeMb(long diskBytes)
    {
        var diskMb = diskBytes / (1024 * 1024);
        return diskMb - 16 <= MaxPartitionMb ? null : MaxPartitionMb;
    }

    /// <summary>The diskpart script that erases disk <paramref name="diskNumber"/> and leaves one
    /// active FAT32 partition labelled BOOTME at <paramref name="letter"/>:.</summary>
    public static string DiskpartScript(int diskNumber, long diskBytes, char letter)
    {
        var size = PartitionSizeMb(diskBytes) is { } mb ? $" size={mb.ToString(CultureInfo.InvariantCulture)}" : "";
        return string.Join("\r\n",
            $"select disk {diskNumber.ToString(CultureInfo.InvariantCulture)}",
            "clean",
            "convert mbr",
            $"create partition primary{size}",
            "select partition 1",
            "active",
            $"format fs=fat32 quick label={VolumeLabel}",
            $"assign letter={char.ToUpperInvariant(letter)}",
            "exit") + "\r\n";
    }

    /// <summary>A drive letter nothing uses, from Z: down.</summary>
    public static char FreeDriveLetter(IEnumerable<char> used)
    {
        var taken = used.Select(char.ToUpperInvariant).ToHashSet();
        for (var c = 'Z'; c >= 'D'; c--)
            if (!taken.Contains(c)) return c;
        throw new IOException("Every drive letter is in use; unplug a drive and try again.");
    }

    // MARK: - The archive

    public static RecoverySourceKind KindOf(string path)
    {
        if (Directory.Exists(path)) return RecoverySourceKind.Folder;
        var name = System.IO.Path.GetFileName(path).ToLowerInvariant();
        if (name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".tgz", StringComparison.Ordinal)) return RecoverySourceKind.TarGz;
        if (name.EndsWith(".tar", StringComparison.Ordinal)) return RecoverySourceKind.Tar;
        if (name.EndsWith(".zip", StringComparison.Ordinal)) return RecoverySourceKind.Zip;
        throw new NotSupportedException("Pick the recovery archive (.tar.gz or .zip) you downloaded from NVIDIA, or the folder you extracted it to.");
    }

    /// <summary>"./a/b" or "a\b" → "a/b".</summary>
    public static string NormalizeEntry(string name)
    {
        var path = name.Replace('\\', '/');
        while (path.StartsWith("./", StringComparison.Ordinal)) path = path[2..];
        return path.TrimStart('/');
    }

    /// <summary>The folder inside the archive that belongs at the root of the stick: the shallowest
    /// one holding an EFI folder; else a single top-level folder wrapping everything; else the top.</summary>
    public static string ContentRoot(IEnumerable<string> entries)
    {
        var paths = entries.Select(NormalizeEntry).Where(p => p.Length > 0).ToList();
        string? best = null;
        var bestDepth = int.MaxValue;
        foreach (var path in paths)
        {
            var segments = path.TrimEnd('/').Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                if (!segments[i].Equals("EFI", StringComparison.OrdinalIgnoreCase)) continue;
                // "EFI" must be a folder: something below it, or a directory entry ("EFI/").
                if (i == segments.Length - 1 && !path.EndsWith('/')) continue;
                if (i < bestDepth)
                {
                    bestDepth = i;
                    best = string.Join('/', segments[..i]);
                }
                break;
            }
        }
        if (best is not null) return best;
        var tops = paths.Select(p => p.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
        var nested = paths.Any(p => p.TrimEnd('/').Contains('/'));
        return tops.Count == 1 && nested ? tops[0] : "";
    }

    /// <summary>The path of <paramref name="entry"/> relative to <paramref name="root"/>, or null when
    /// it lies outside it or tries to escape (".." or an absolute path).</summary>
    public static string? RelativeTo(string root, string entry)
    {
        var path = NormalizeEntry(entry);
        if (root.Length > 0)
        {
            if (!path.StartsWith(root + "/", StringComparison.Ordinal)) return null;
            path = path[(root.Length + 1)..];
        }
        path = path.TrimEnd('/');
        if (path.Length == 0) return null;
        var segments = path.Split('/');
        if (segments.Any(s => s is ".." or "." || s.Length == 0 || s.Contains(':'))) return null;
        return path;
    }

    private sealed record Entry(string Name, long Length, bool IsDirectory);

    /// <summary>Look through the archive (for .tar.gz that means reading all of it once) to learn
    /// its size, its largest file and where its content starts — before anything is erased.</summary>
    public static async Task<RecoverySource> InspectAsync(string path, IProgress<RecoveryProgress>? progress = null,
                                                          CancellationToken cancellationToken = default)
    {
        var kind = KindOf(path);
        var entries = await Task.Run(() => List(path, kind, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        var root = ContentRoot(entries.Select(e => e.IsDirectory ? e.Name.TrimEnd('/') + "/" : e.Name));
        var files = entries.Where(e => !e.IsDirectory && RelativeTo(root, e.Name) is not null).ToList();
        var largest = files.MaxBy(e => e.Length);
        var hasEfi = entries.Any(e => RelativeTo(root, e.Name) is { } rel
                                      && rel.Split('/')[0].Equals("EFI", StringComparison.OrdinalIgnoreCase));
        return new RecoverySource(path, kind, root, files.Sum(e => e.Length), files.Count, largest?.Length ?? 0, largest?.Name, hasEfi);
    }

    private static List<Entry> List(string path, RecoverySourceKind kind, IProgress<RecoveryProgress>? progress, CancellationToken cancellationToken)
    {
        var entries = new List<Entry>();
        switch (kind)
        {
            case RecoverySourceKind.Folder:
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    entries.Add(new Entry(System.IO.Path.GetRelativePath(path, file).Replace('\\', '/'), new FileInfo(file).Length, false));
                }
                foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                    entries.Add(new Entry(System.IO.Path.GetRelativePath(path, directory).Replace('\\', '/') + "/", 0, true));
                break;
            case RecoverySourceKind.Zip:
                using (var zip = ZipFile.OpenRead(path))
                {
                    foreach (var entry in zip.Entries)
                    {
                        var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
                        entries.Add(new Entry(NormalizeEntry(entry.FullName), entry.Length, isDirectory));
                    }
                }
                break;
            default:
                using (var file = File.OpenRead(path))
                {
                    var total = file.Length;
                    using var reader = new TarReader(Decompressed(file, kind));
                    while (reader.GetNextEntry() is { } entry)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (entry.EntryType == TarEntryType.Directory) entries.Add(new Entry(NormalizeEntry(entry.Name) + "/", 0, true));
                        else if (IsRegular(entry.EntryType)) entries.Add(new Entry(NormalizeEntry(entry.Name), entry.Length, false));
                        progress?.Report(new RecoveryProgress("Checking the recovery file", file.Position, total, entry.Name));
                    }
                }
                break;
        }
        return entries;
    }

    private static Stream Decompressed(FileStream file, RecoverySourceKind kind) =>
        kind == RecoverySourceKind.TarGz ? new GZipStream(file, CompressionMode.Decompress, leaveOpen: true) : file;

    private static bool IsRegular(TarEntryType type) =>
        type is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile;

    /// <summary>Copy the archive's content (from its <see cref="RecoverySource.Root"/>) into
    /// <paramref name="destination"/>. Links and special files are skipped (FAT32 has none); any
    /// entry that would land outside the destination is refused.</summary>
    public static async Task ExtractAsync(RecoverySource source, string destination, IProgress<RecoveryProgress>? progress = null,
                                          Func<bool>? cancelRequested = null, CancellationToken cancellationToken = default)
    {
        await Task.Run(() => Extract(source, destination, progress, cancelRequested, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static void Extract(RecoverySource source, string destination, IProgress<RecoveryProgress>? progress, Func<bool>? cancelRequested,
                                CancellationToken cancellationToken)
    {
        const string stage = "Copying the recovery files";
        var done = 0L;
        var buffer = new byte[1024 * 1024];
        var lastCheck = Stopwatch.StartNew();
        var full = System.IO.Path.GetFullPath(destination);

        void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cancelRequested is not null && lastCheck.ElapsedMilliseconds > 500)
            {
                lastCheck.Restart();
                if (cancelRequested()) throw new OperationCanceledException("Stopped.");
            }
        }

        string? Target(string name)
        {
            if (RelativeTo(source.Root, name) is not { } relative) return null;
            var target = System.IO.Path.GetFullPath(System.IO.Path.Combine(full, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            return target.StartsWith(full, StringComparison.OrdinalIgnoreCase) ? target : null;
        }

        void Copy(Stream input, string name, long length)
        {
            if (Target(name) is not { } target) return;
            if (length >= Fat32MaxFile) throw new IOException($"{name} is larger than 4 GB, which a FAT32 stick can't hold.");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024);
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                Check();
                output.Write(buffer, 0, read);
                done += read;
                progress?.Report(new RecoveryProgress(stage, done, source.TotalBytes, name));
            }
        }

        Directory.CreateDirectory(full);
        switch (source.Kind)
        {
            case RecoverySourceKind.Folder:
                foreach (var file in Directory.EnumerateFiles(source.Path, "*", SearchOption.AllDirectories))
                {
                    Check();
                    var name = System.IO.Path.GetRelativePath(source.Path, file).Replace('\\', '/');
                    using var input = File.OpenRead(file);
                    Copy(input, name, input.Length);
                }
                break;
            case RecoverySourceKind.Zip:
                using (var zip = ZipFile.OpenRead(source.Path))
                {
                    foreach (var entry in zip.Entries)
                    {
                        Check();
                        var name = NormalizeEntry(entry.FullName);
                        if (name.EndsWith('/'))
                        {
                            if (Target(name) is { } directory) Directory.CreateDirectory(directory);
                            continue;
                        }
                        using var input = entry.Open();
                        Copy(input, name, entry.Length);
                    }
                }
                break;
            default:
                using (var file = File.OpenRead(source.Path))
                {
                    using var reader = new TarReader(Decompressed(file, source.Kind));
                    while (reader.GetNextEntry() is { } entry)
                    {
                        Check();
                        var name = NormalizeEntry(entry.Name);
                        if (entry.EntryType == TarEntryType.Directory)
                        {
                            if (Target(name) is { } directory) Directory.CreateDirectory(directory);
                            continue;
                        }
                        if (!IsRegular(entry.EntryType) || entry.DataStream is null) continue;
                        Copy(entry.DataStream, name, entry.Length);
                    }
                }
                break;
        }
    }

    /// <summary>EFI\BOOT\recovery.txt with the word RECOVERY: what tells the stick's boot menu this
    /// is a recovery (reinstall) stick.</summary>
    public static string WriteRecoveryMarker(string root)
    {
        var folder = System.IO.Path.Combine(root, "EFI", "BOOT");
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, "recovery.txt");
        File.WriteAllText(path, "RECOVERY\n", TextUtil.Utf8NoBom);
        return path;
    }

    // MARK: - Processes

    internal static async Task<(int Exit, string Output, string Error)> RunAsync(string file, string arguments, TimeSpan timeout,
                                                                               CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo(file, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(info) ?? throw new IOException($"Couldn't start {file}.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            throw;
        }
        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }

    [SupportedOSPlatform("windows")]
    internal static Task<(int Exit, string Output, string Error)> PowerShellAsync(string script, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Encoded, so the script needs no quoting; UTF-8 output so names survive.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("[Console]::OutputEncoding = [Text.Encoding]::UTF8\n" + script));
        var powershell = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        return RunAsync(powershell, $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}", timeout, cancellationToken);
    }
}

// MARK: - The elevated helper

/// <summary>What the elevated helper is asked to do. The disk is identified three ways (number,
/// serial, size) so a stick swapped in the meantime is noticed.</summary>
public sealed record RecoveryUsbJob(
    int DiskNumber, string? SerialNumber, long Size, string FriendlyName, string Source, string Root, long TotalBytes,
    string StatusPath, string CancelPath);

/// <summary>The helper's progress, rewritten as a small JSON file the app polls.</summary>
public sealed record RecoveryUsbStatus(
    string State, string Stage, string Message, long Done = 0, long Total = 0, string? Error = null, string? DriveLetter = null)
{
    [JsonIgnore] public bool IsRunning => State == "running";
    [JsonIgnore] public bool IsDone => State == "done";
    [JsonIgnore] public bool IsFailed => State == "failed";
    [JsonIgnore] public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Done / Total, 0, 1);
}

public static class RecoveryUsbWriter
{
    /// <summary>DSH.exe --write-recovery-usb &lt;job.json&gt;</summary>
    public const string HelperArgument = "--write-recovery-usb";

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    /// <summary>When <paramref name="args"/> ask for the helper, run the job and return its exit code;
    /// otherwise null (start the app normally).</summary>
    public static int? TryRunHelper(string[] args)
    {
        var index = Array.FindIndex(args, a => a.Equals(HelperArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        if (index + 1 >= args.Length) return 2;
        return Task.Run(() => RunJobAsync(args[index + 1])).GetAwaiter().GetResult();
    }

    /// <summary>Everything the elevated helper does, start to finish. Returns 0 on success.</summary>
    public static async Task<int> RunJobAsync(string jobPath)
    {
        RecoveryUsbJob job;
        try
        {
            job = JsonSerializer.Deserialize<RecoveryUsbJob>(await File.ReadAllTextAsync(jobPath).ConfigureAwait(false), Json)
                  ?? throw new InvalidDataException("empty job");
        }
        catch (Exception)
        {
            return 2;
        }
        void Report(RecoveryUsbStatus status) => WriteStatus(job.StatusPath, status);
        bool Cancelled() => File.Exists(job.CancelPath);
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Writing a recovery USB needs Windows.");
            await WriteAsync(job, Report, Cancelled).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Report(new RecoveryUsbStatus("failed", "Stopped", "Stopped before it finished. The stick isn't usable yet — write it again.",
                Error: "Stopped."));
            return 1;
        }
        catch (Exception ex)
        {
            Report(new RecoveryUsbStatus("failed", "Failed", ex.Message, Error: ex.Message));
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task WriteAsync(RecoveryUsbJob job, Action<RecoveryUsbStatus> report, Func<bool> cancelled)
    {
        report(new RecoveryUsbStatus("running", "Checking the USB drive", "Making sure it's the same stick you chose…"));
        var disks = await RecoveryUsb.ListDisksAsync().ConfigureAwait(false);
        var disk = disks.FirstOrDefault(d => d.Number == job.DiskNumber);
        var same = disk is not null && disk.Size == job.Size
                   && (job.SerialNumber is null || string.Equals(disk.SerialNumber, job.SerialNumber, StringComparison.Ordinal));
        if (!same) throw new IOException("The USB drive changed since you chose it (or was unplugged). Nothing was erased.");
        if (RecoveryUsb.Ineligible(disk!, RecoveryUsb.SystemDriveLetter()) is { } reason)
            throw new IOException($"DSH won't erase that drive: {reason} Nothing was erased.");
        if (cancelled()) throw new OperationCanceledException();

        var letter = RecoveryUsb.FreeDriveLetter(DriveInfo.GetDrives().Select(d => d.Name[0]));
        report(new RecoveryUsbStatus("running", "Erasing and formatting", $"Erasing {disk!.Describe()} and formatting it as FAT32 ({RecoveryUsb.VolumeLabel})…"));
        var script = Path.Combine(Path.GetDirectoryName(job.StatusPath)!, "diskpart.txt");
        await File.WriteAllTextAsync(script, RecoveryUsb.DiskpartScript(disk.Number, disk.Size, letter), Encoding.ASCII).ConfigureAwait(false);
        var diskpart = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "diskpart.exe");
        var (exit, output, error) = await RecoveryUsb.RunAsync(diskpart, $"/s \"{script}\"", TimeSpan.FromMinutes(10), CancellationToken.None).ConfigureAwait(false);
        if (exit != 0)
            throw new IOException($"Windows couldn't format the stick (diskpart exit {exit}). {TextUtil.Suffix((output + error).Trim(), 400)}");

        var root = $"{letter}:\\";
        for (var i = 0; i < 60 && !Directory.Exists(root); i++) await Task.Delay(500).ConfigureAwait(false);
        var drive = new DriveInfo(root);
        if (!drive.IsReady || !string.Equals(drive.DriveFormat, "FAT32", StringComparison.OrdinalIgnoreCase))
            throw new IOException($"The stick was formatted but {root} isn't a FAT32 drive. Unplug it, plug it back in, and try again.");

        var source = new RecoverySource(job.Source, RecoveryUsb.KindOf(job.Source), job.Root, job.TotalBytes, 1, 0, null, true);
        var last = Stopwatch.StartNew();
        var progress = new SyncProgress(p =>
        {
            if (last.ElapsedMilliseconds < 250) return;
            last.Restart();
            report(new RecoveryUsbStatus("running", p.Stage, p.CurrentFile ?? "", p.Done, p.Total, DriveLetter: root));
        });
        await RecoveryUsb.ExtractAsync(source, root, progress, cancelled).ConfigureAwait(false);
        RecoveryUsb.WriteRecoveryMarker(root);

        report(new RecoveryUsbStatus("running", "Finishing", "Making sure everything is written to the stick…", job.TotalBytes, job.TotalBytes, DriveLetter: root));
        try
        {
            await RecoveryUsb.PowerShellAsync($"Write-VolumeCache -DriveLetter {letter}", TimeSpan.FromMinutes(5), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: "Safely remove" flushes too.
        }
        report(new RecoveryUsbStatus("done", "Done", $"The recovery stick is ready ({root}).", job.TotalBytes, job.TotalBytes, DriveLetter: root));
    }

    /// <summary>Progress reported on the calling thread (the helper has no UI thread to post to).</summary>
    private sealed class SyncProgress(Action<RecoveryProgress> report) : IProgress<RecoveryProgress>
    {
        public void Report(RecoveryProgress value) => report(value);
    }

    public static void WriteStatus(string path, RecoveryUsbStatus status)
    {
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(status, Json));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The app reads the next one.
        }
    }

    public static RecoveryUsbStatus? ReadStatus(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<RecoveryUsbStatus>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Start the elevated helper for <paramref name="job"/> (Windows shows its "allow this
    /// app to make changes" prompt) and follow its status file until it exits. Cancelling asks the
    /// helper to stop at the next file. Throws <see cref="OperationCanceledException"/> if the user
    /// declines the prompt.</summary>
    [SupportedOSPlatform("windows")]
    public static async Task<RecoveryUsbStatus> RunElevatedAsync(RecoveryUsbJob job, Action<RecoveryUsbStatus> onStatus,
                                                                 CancellationToken cancellationToken = default)
    {
        var jobPath = Path.Combine(Path.GetDirectoryName(job.StatusPath)!, "job.json");
        Directory.CreateDirectory(Path.GetDirectoryName(jobPath)!);
        await File.WriteAllTextAsync(jobPath, JsonSerializer.Serialize(job, Json), CancellationToken.None).ConfigureAwait(false);
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Can't find DSH.exe to start the helper.");
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(exe, $"{HelperArgument} \"{jobPath}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("Windows' permission prompt was declined, so nothing was erased.");
        }
        if (process is null) throw new IOException("The helper didn't start.");
        using (process)
        {
            var cancelSent = false;
            RecoveryUsbStatus? last = null;
            while (!process.HasExited)
            {
                if (cancellationToken.IsCancellationRequested && !cancelSent)
                {
                    cancelSent = true;
                    try { await File.WriteAllTextAsync(job.CancelPath, "stop", CancellationToken.None).ConfigureAwait(false); } catch (IOException) { }
                }
                if (ReadStatus(job.StatusPath) is { } status && status != last) onStatus(last = status);
                await Task.Delay(300, CancellationToken.None).ConfigureAwait(false);
            }
            var final = ReadStatus(job.StatusPath)
                        ?? new RecoveryUsbStatus("failed", "Failed", "The helper stopped without saying why.", Error: $"exit code {process.ExitCode}");
            if (final.IsRunning) final = final with { State = "failed", Error = "The helper stopped before it finished." };
            onStatus(final);
            return final;
        }
    }

    /// <summary>A fresh folder for one job's files (job, status, diskpart script).</summary>
    public static string NewJobFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "dsh-recovery-usb", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
