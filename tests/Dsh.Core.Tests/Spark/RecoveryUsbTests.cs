using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Dsh.Core.Tests;

public sealed class RecoveryUsbTests
{
    // MARK: - Which disks

    private const string DisksJson = """
        [{"Number":0,"FriendlyName":"Samsung SSD 990 PRO 2TB","Size":2000398934016,"SerialNumber":"S6Z2NJ0W","BusType":"NVMe","IsBoot":true,"IsSystem":true,"IsOffline":false,"IsReadOnly":false,"PartitionStyle":"GPT","DriveLetters":["C"],"Labels":["Windows"]},
         {"Number":1,"FriendlyName":"SanDisk Ultra","Size":30752636928,"SerialNumber":"4C530001230519115044 ","BusType":"USB","IsBoot":false,"IsSystem":false,"IsOffline":false,"IsReadOnly":false,"PartitionStyle":"MBR","DriveLetters":["E"],"Labels":["SANDISK"]},
         {"Number":2,"FriendlyName":"WD Elements 25A2","Size":2000365289472,"SerialNumber":"WX12","BusType":"USB","IsBoot":false,"IsSystem":false,"IsOffline":false,"IsReadOnly":false,"PartitionStyle":"GPT","DriveLetters":["F"],"Labels":["Backup"]},
         {"Number":3,"FriendlyName":"Kingston DataTraveler","Size":7759462400,"SerialNumber":"","BusType":7,"IsBoot":false,"IsSystem":false,"IsOffline":false,"IsReadOnly":false,"PartitionStyle":"RAW","DriveLetters":[],"Labels":[]},
         {"Number":4,"FriendlyName":"Old stick","Size":4009754624,"SerialNumber":"X","BusType":"USB","IsBoot":false,"IsSystem":false,"IsOffline":false,"IsReadOnly":false,"PartitionStyle":"MBR","DriveLetters":"G","Labels":"OLD"},
         {"Number":5,"FriendlyName":"Locked stick","Size":16008609792,"SerialNumber":"L","BusType":"USB","IsBoot":false,"IsSystem":false,"IsOffline":false,"IsReadOnly":true,"PartitionStyle":"MBR","DriveLetters":[],"Labels":[]}]
        """;

    [Fact]
    public void ParsesTheDiskListing()
    {
        var disks = RecoveryUsb.ParseDisks(DisksJson);
        Assert.Equal(6, disks.Count);
        var sandisk = disks[1];
        Assert.Equal(1, sandisk.Number);
        Assert.Equal("SanDisk Ultra", sandisk.FriendlyName);
        Assert.Equal(30752636928, sandisk.Size);
        Assert.Equal("4C530001230519115044", sandisk.SerialNumber);
        Assert.True(sandisk.IsUsb);
        Assert.Equal(['E'], sandisk.DriveLetters);
        Assert.Equal(["SANDISK"], sandisk.Labels);
        Assert.Equal("SanDisk Ultra 30.8 GB (E:)", sandisk.Describe());

        Assert.True(disks[3].IsUsb);             // BusType 7 = USB in raw CIM output
        Assert.Null(disks[3].SerialNumber);
        Assert.Equal(['G'], disks[4].DriveLetters); // PowerShell writes a one-item array as a bare value
        Assert.Equal(["OLD"], disks[4].Labels);
    }

    [Fact]
    public void ASingleDiskIsAnObjectNotAnArray()
    {
        var one = RecoveryUsb.ParseDisks("""{"Number":1,"FriendlyName":"Stick","Size":16000000000,"BusType":"USB"}""");
        Assert.Equal("Stick", Assert.Single(one).FriendlyName);
        Assert.Empty(RecoveryUsb.ParseDisks("not json"));
        Assert.Empty(RecoveryUsb.ParseDisks(""));
    }

    [Fact]
    public void OnlyRemovableUsbSticksAreOffered()
    {
        var disks = RecoveryUsb.ParseDisks(DisksJson);
        var eligible = RecoveryUsb.Eligible(disks, 'C');
        Assert.Equal([1, 3], eligible.Select(d => d.Number));

        Assert.Equal("It isn't a USB drive.", RecoveryUsb.Ineligible(disks[0]));
        Assert.Contains("larger than 256 GB", RecoveryUsb.Ineligible(disks[2]));
        Assert.Contains("too small", RecoveryUsb.Ineligible(disks[4]));
        Assert.Contains("write-protected", RecoveryUsb.Ineligible(disks[5]));
    }

    [Fact]
    public void NeverTheDiskWindowsRunsFrom()
    {
        var usbBoot = new UsbDisk { Number = 1, BusType = "USB", Size = 32_000_000_000, IsBoot = true };
        Assert.Equal("Windows starts from this drive.", RecoveryUsb.Ineligible(usbBoot));
        var usbSystem = usbBoot with { IsBoot = false, IsSystem = true };
        Assert.NotNull(RecoveryUsb.Ineligible(usbSystem));
        // Windows To Go, or a PC whose system drive letter isn't C:.
        var holdsWindows = usbBoot with { IsBoot = false, DriveLetters = ['D'] };
        Assert.Equal("It holds Windows (D:).", RecoveryUsb.Ineligible(holdsWindows, 'd'));
        Assert.Null(RecoveryUsb.Ineligible(holdsWindows, 'C'));
        var offline = usbBoot with { IsBoot = false, IsOffline = true };
        Assert.NotNull(RecoveryUsb.Ineligible(offline));
    }

    // MARK: - diskpart

    [Fact]
    public void PartitionsAreCappedFor32BitFat()
    {
        Assert.Null(RecoveryUsb.PartitionSizeMb(16_008_609_792));    // a "16 GB" stick: all of it
        Assert.Null(RecoveryUsb.PartitionSizeMb(30_752_636_928));    // a "32 GB" stick (28.6 GiB): all of it
        Assert.Equal(31 * 1024, RecoveryUsb.PartitionSizeMb(64_023_257_088));
        Assert.Equal(31 * 1024, RecoveryUsb.PartitionSizeMb(256_060_514_304));
    }

    [Fact]
    public void TheDiskpartScript()
    {
        var script = RecoveryUsb.DiskpartScript(3, 64_023_257_088, 'r');
        Assert.Equal(
            "select disk 3\r\nclean\r\nconvert mbr\r\ncreate partition primary size=31744\r\nselect partition 1\r\nactive\r\n" +
            "format fs=fat32 quick label=BOOTME\r\nassign letter=R\r\nexit\r\n", script);
        Assert.Contains("create partition primary\r\n", RecoveryUsb.DiskpartScript(1, 16_008_609_792, 'Z'));
        // It selects exactly one disk, first, and nothing else.
        Assert.Single(script.Split("\r\n"), l => l.StartsWith("select disk", StringComparison.Ordinal));
        Assert.StartsWith("select disk 3\r\n", script);
    }

    [Fact]
    public void PicksAFreeDriveLetter()
    {
        Assert.Equal('Z', RecoveryUsb.FreeDriveLetter(['C', 'D']));
        Assert.Equal('X', RecoveryUsb.FreeDriveLetter(['c', 'z', 'Y']));
        Assert.Throws<IOException>(() => RecoveryUsb.FreeDriveLetter(Enumerable.Range('A', 26).Select(i => (char)i)));
    }

    // MARK: - The archive

    [Theory]
    [InlineData(new[] { "EFI/BOOT/BOOTAA64.EFI", "boot/grub/grub.cfg", "images/fs.sqfs" }, "")]
    [InlineData(new[] { "dgx-spark-recovery/", "dgx-spark-recovery/EFI/BOOT/BOOTAA64.EFI", "dgx-spark-recovery/boot/grub/grub.cfg" }, "dgx-spark-recovery")]
    [InlineData(new[] { "CreateUSBKey.cmd", "CreateUSBKey.ps1", "usb/efi/boot/bootaa64.efi", "usb/casper/filesystem.squashfs" }, "usb")]
    [InlineData(new[] { "./EFI/BOOT/x.efi", "./README" }, "")]
    [InlineData(new[] { "wrapper/a.txt", "wrapper/b/c.txt" }, "wrapper")]
    [InlineData(new[] { "a.txt", "b/c.txt" }, "")]
    [InlineData(new[] { "notes/EFI" }, "notes")]   // a file named EFI isn't the EFI folder; one top folder wraps it
    public void FindsWhereTheStickContentStarts(string[] entries, string expected) =>
        Assert.Equal(expected, RecoveryUsb.ContentRoot(entries));

    [Theory]
    [InlineData("usb", "usb/EFI/BOOT/x.efi", "EFI/BOOT/x.efi")]
    [InlineData("usb", "CreateUSBKey.cmd", null)]
    [InlineData("", "./EFI/", "EFI")]
    [InlineData("", "../../Windows/System32/evil.dll", null)]
    [InlineData("", "EFI/../../evil", null)]
    [InlineData("", "C:/Windows/evil", null)]
    [InlineData("", "/etc/passwd", "etc/passwd")]
    public void EntriesStayInsideTheStick(string root, string entry, string? expected) =>
        Assert.Equal(expected, RecoveryUsb.RelativeTo(root, entry));

    [Theory]
    [InlineData("recovery.tar.gz", RecoverySourceKind.TarGz)]
    [InlineData("RECOVERY.TGZ", RecoverySourceKind.TarGz)]
    [InlineData("recovery.tar", RecoverySourceKind.Tar)]
    [InlineData("recovery.zip", RecoverySourceKind.Zip)]
    public void KnowsArchiveKinds(string name, RecoverySourceKind kind) => Assert.Equal(kind, RecoveryUsb.KindOf(name));

    [Fact]
    public void OtherFilesAreNotRecoveryMedia() => Assert.Throws<NotSupportedException>(() => RecoveryUsb.KindOf("dgx.iso"));

    private static readonly (string Name, string Content)[] Media =
    [
        ("dgx-spark-recovery/CreateUSBKey.cmd", "@echo off"),
        ("dgx-spark-recovery/usb/EFI/BOOT/BOOTAA64.EFI", "efi-binary"),
        ("dgx-spark-recovery/usb/boot/grub/grub.cfg", "menuentry 'Install DGX OS'"),
        ("dgx-spark-recovery/usb/images/part-00.img", new string('x', 5000)),
    ];

    private static string TarGz(TempDirectory temp)
    {
        var path = temp["recovery.tar.gz"];
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "dgx-spark-recovery/usb/"));
        foreach (var (name, content) in Media)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content)) };
            writer.WriteEntry(entry);
        }
        // A symlink: FAT32 can't hold it, so it is skipped.
        writer.WriteEntry(new PaxTarEntry(TarEntryType.SymbolicLink, "dgx-spark-recovery/usb/latest") { LinkName = "images/part-00.img" });
        return path;
    }

    private static string Zip(TempDirectory temp)
    {
        var path = temp["recovery.zip"];
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in Media)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.UTF8.GetBytes(content));
        }
        return path;
    }

    private static string Folder(TempDirectory temp)
    {
        foreach (var (name, content) in Media) temp.Write("extracted/" + name, content);
        return temp["extracted"];
    }

    private static void AssertStick(string root)
    {
        Assert.Equal("efi-binary", File.ReadAllText(Path.Combine(root, "EFI", "BOOT", "BOOTAA64.EFI")));
        Assert.Equal("menuentry 'Install DGX OS'", File.ReadAllText(Path.Combine(root, "boot", "grub", "grub.cfg")));
        Assert.Equal(5000, new FileInfo(Path.Combine(root, "images", "part-00.img")).Length);
        Assert.False(File.Exists(Path.Combine(root, "CreateUSBKey.cmd")));
        Assert.False(File.Exists(Path.Combine(root, "latest")));
        Assert.False(Directory.Exists(Path.Combine(root, "dgx-spark-recovery")));
    }

    [Theory]
    [InlineData("tar.gz")]
    [InlineData("zip")]
    [InlineData("folder")]
    public async Task InspectsAndExtractsToTheStickLayout(string kind)
    {
        using var temp = new TempDirectory();
        var source = kind switch { "tar.gz" => TarGz(temp), "zip" => Zip(temp), _ => Folder(temp) };

        var inspected = await RecoveryUsb.InspectAsync(source);
        Assert.Equal("dgx-spark-recovery/usb", inspected.Root);
        Assert.Equal(3, inspected.FileCount);
        Assert.Equal(10 + 26 + 5000, inspected.TotalBytes);
        Assert.Equal(5000, inspected.LargestFile);
        Assert.EndsWith("part-00.img", inspected.LargestName);
        Assert.True(inspected.HasEfi);
        Assert.Null(inspected.Problem(16_000_000_000));

        var stick = temp["stick"];
        var reports = new List<RecoveryProgress>();
        await RecoveryUsb.ExtractAsync(inspected, stick, new SynchronousProgress<RecoveryProgress>(reports.Add));
        AssertStick(stick);
        Assert.Equal(inspected.TotalBytes, reports[^1].Done);
        Assert.Equal(1.0, reports[^1].Fraction);
    }

    [Fact]
    public void ProblemsAreFoundBeforeAnythingIsErased()
    {
        var huge = new RecoverySource("x.tar.gz", RecoverySourceKind.TarGz, "", 5_000_000_000, 2, 4L * 1024 * 1024 * 1024, "images/big.img", true);
        Assert.Contains("4 GB", huge.Problem(32_000_000_000));
        var tooBig = huge with { LargestFile = 1000, TotalBytes = 20_000_000_000 };
        Assert.Contains("more than this stick can hold", tooBig.Problem(16_000_000_000));
        var empty = huge with { FileCount = 0 };
        Assert.Contains("empty", empty.Problem(16_000_000_000));
    }

    [Fact]
    public async Task StopsWhenAskedTo()
    {
        using var temp = new TempDirectory();
        var inspected = await RecoveryUsb.InspectAsync(Folder(temp));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RecoveryUsb.ExtractAsync(inspected, temp["stick"], cancellationToken: cts.Token));
    }

    [Fact]
    public void WritesTheRecoveryMarker()
    {
        using var temp = new TempDirectory();
        var path = RecoveryUsb.WriteRecoveryMarker(temp.Path);
        Assert.Equal(Path.Combine(temp.Path, "EFI", "BOOT", "recovery.txt"), path);
        Assert.Equal("RECOVERY\n", File.ReadAllText(path));
        Assert.Equal(Encoding.ASCII.GetBytes("RECOVERY\n"), File.ReadAllBytes(path)); // no BOM
    }

    // MARK: - The helper's plumbing

    [Fact]
    public void StatusFilesRoundTrip()
    {
        using var temp = new TempDirectory();
        var path = temp["status.json"];
        Assert.Null(RecoveryUsbWriter.ReadStatus(path));
        var status = new RecoveryUsbStatus("running", "Copying the recovery files", "images/part-00.img", 50, 200, DriveLetter: "R:\\");
        RecoveryUsbWriter.WriteStatus(path, status);
        Assert.Equal(status, RecoveryUsbWriter.ReadStatus(path));
        Assert.Equal(0.25, status.Fraction);
        File.WriteAllText(path, "{ half written");
        Assert.Null(RecoveryUsbWriter.ReadStatus(path));
    }

    [Fact]
    public void TheHelperOnlyRunsWhenAskedFor()
    {
        Assert.Null(RecoveryUsbWriter.TryRunHelper([]));
        Assert.Null(RecoveryUsbWriter.TryRunHelper(["--self-test", "out"]));
        Assert.Equal(2, RecoveryUsbWriter.TryRunHelper([RecoveryUsbWriter.HelperArgument]));
    }

    [UnixFact]
    public async Task TheHelperRefusesOffWindows()
    {
        using var temp = new TempDirectory();
        var job = new RecoveryUsbJob(1, "S", 16_000_000_000, "Stick", temp["x.zip"], "", 10, temp["status.json"], temp["cancel"]);
        var jobPath = temp.Write("job.json", System.Text.Json.JsonSerializer.Serialize(job,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
        Assert.Equal(1, await RecoveryUsbWriter.RunJobAsync(jobPath));
        var status = RecoveryUsbWriter.ReadStatus(temp["status.json"]);
        Assert.True(status?.IsFailed);
        Assert.Contains("Windows", status?.Error);
    }
}
