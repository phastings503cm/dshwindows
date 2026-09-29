using System.IO;
using System.Windows;
using System.Windows.Controls;
using Dsh.App.Infrastructure;
using Dsh.App.Views.Dialogs;
using Dsh.Core;
using Microsoft.Win32;
using static Dsh.App.Views.Guide.GuideVisuals;

namespace Dsh.App.Views.Guide;

// MARK: - Starting fresh: the recovery USB stick

public sealed partial class SparkGuide
{
    public const string RecoveryDocs = "https://docs.nvidia.com/dgx/dgx-spark/system-recovery.html";

    private RecoverySource? _source;
    private string? _sourceProblem;
    private bool _inspecting;
    private bool _acceptNoEfi;
    private readonly ProgressBar _inspectBar = new() { Height = 6, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 4, 0, 4) };

    private List<UsbDisk>? _disks;
    private string? _disksError;
    private bool _listing;
    private UsbDisk? _chosenDisk;
    private bool _eraseConfirmed;
    private bool _writing;
    private bool _usbDone;
    private RecoveryUsbStatus? _usbStatus;
    private CancellationTokenSource? _usbCts;
    private readonly ProgressBar _usbBar = new() { Height = 8, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 6, 0, 6) };

    // MARK: - Download

    private UIElement DownloadPage()
    {
        var panel = Ui.Stack(
            Picture("download", "NVIDIA's recovery download page and the Downloads folder."),
            Heading("Download NVIDIA's recovery file"),
            Steps(
                "Open NVIDIA's DGX Spark **System Recovery** page and follow its link to the recovery media. You may need to sign in with a free NVIDIA account.",
                "Download the recovery file. It's several gigabytes — let it finish.",
                "Tell DSH where it is: choose the downloaded file (it usually ends in .tar.gz), or the folder you unpacked it to."),
            Ui.Buttons(
                Ui.Button("Open NVIDIA's recovery page", () => ShellIntegration.Open(RecoveryDocs)),
                Ui.Button("Choose the file…", ChooseSource, accent: _source is null),
                Ui.Button("Choose a folder…", ChooseSourceFolder)));

        if (_inspecting)
        {
            panel.Children.Add(Busy("Checking the recovery file (this reads all of it, so it can take a minute)…"));
            panel.Children.Add(Detach(_inspectBar));
        }
        else if (_sourceProblem is { } problem)
        {
            panel.Children.Add(Note(Tone.Error, problem, "This file can't be used"));
        }
        else if (_source is { } source)
        {
            var name = Path.GetFileName(source.Path.TrimEnd('\\', '/'));
            var summary = $"{name} — {source.FileCount:N0} files, {RecoveryUsb.FormatSize(source.TotalBytes)}.";
            if (source.HasEfi)
            {
                panel.Children.Add(Note(Tone.Success, summary + " It has the startup (EFI) files a recovery stick needs.", "Looks right"));
            }
            else
            {
                var accept = Ui.Check("It's the file from NVIDIA's recovery page — use it anyway", _acceptNoEfi, value =>
                {
                    _acceptNoEfi = value;
                    _host.RefreshChrome();
                });
                accept.Margin = new Thickness(0, 6, 0, 0);
                panel.Children.Add(Note(Tone.Warning,
                    summary + " It has no EFI folder, which a bootable stick needs — are you sure this is the recovery media, not the installation guide or another download?",
                    "This might not be the recovery file", accept));
            }
        }
        panel.Children.Add(Aside("The recovery file holds a complete copy of DGX OS plus NVIDIA's installer. Before anything is erased, DSH reads it through once to check that every file fits on a FAT32 stick (none may be over 4 GB) and to find the folder that belongs at the top of the stick."));
        panel.Children.Add(Stuck("Can't find the file? Look in your Downloads folder — the name usually mentions \"recovery\". Don't use the file before it has finished downloading."));
        return panel;
    }

    private void ChooseSource()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose the DGX Spark recovery file",
            Filter = "Recovery archive (*.tar.gz;*.tgz;*.tar;*.zip)|*.tar.gz;*.tgz;*.tar;*.zip|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        if (dialog.ShowDialog(_host.Window) == true) _ = InspectAsync(dialog.FileName);
    }

    private void ChooseSourceFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose the folder you unpacked the recovery file to" };
        if (dialog.ShowDialog(_host.Window) == true) _ = InspectAsync(dialog.FolderName);
    }

    private async Task InspectAsync(string path)
    {
        _inspecting = true;
        _source = null;
        _sourceProblem = null;
        _acceptNoEfi = false;
        _inspectBar.Value = 0;
        _host.Render();
        try
        {
            var progress = new Progress<RecoveryProgress>(p => _inspectBar.Value = p.Fraction);
            var source = await RecoveryUsb.InspectAsync(path, progress);
            _source = source;
            _sourceProblem = source.Problem(RecoveryUsb.MaxPartitionMb * 1024L * 1024L);
            _state.RecoverySource = path;
            _state.Save();
        }
        catch (Exception error)
        {
            _sourceProblem = $"DSH couldn't read it: {error.Message}";
        }
        finally
        {
            _inspecting = false;
            RenderIf(GuidePage.Download);
        }
    }

    // MARK: - Write the stick

    private async Task ListDisksAsync()
    {
        _listing = true;
        _disksError = null;
        RenderIf(GuidePage.WriteUsb);
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Making a USB stick needs Windows.");
            _disks = (await RecoveryUsb.ListDisksAsync()).ToList();
            if (_chosenDisk is { } chosen && !_disks.Any(d => d.Number == chosen.Number && d.Size == chosen.Size)) _chosenDisk = null;
        }
        catch (Exception error)
        {
            _disks = [];
            _disksError = error.Message;
        }
        finally
        {
            _listing = false;
            RenderIf(GuidePage.WriteUsb);
        }
    }

    private UIElement WriteUsbPage()
    {
        var panel = Ui.Stack(
            Picture("usb-writer", "DSH writing the recovery stick, with a warning that everything on it is erased."),
            Heading("Make the recovery stick"),
            Lead("Plug a USB stick of 16 GB or more into this PC. **Everything on it will be erased**, so copy off anything you want to keep first."));

        if (_usbDone)
        {
            panel.Children.Add(Note(Tone.Success,
                $"The recovery stick is ready{(_usbStatus?.DriveLetter is { } letter ? $" ({letter})" : "")}. In File Explorer, right-click it and choose Eject, then unplug it.",
                "Done"));
        }
        else if (_writing)
        {
            var status = _usbStatus;
            panel.Children.Add(Ui.Card(Ui.Stack(
                Ui.Text(status?.Stage ?? "Starting…", 13.5, FontWeights.SemiBold),
                Ui.Secondary(status is { Total: > 0 } s ? $"{RecoveryUsb.FormatSize(s.Done)} of {RecoveryUsb.FormatSize(s.Total)} · {s.Message}" : status?.Message ?? "Waiting for Windows' permission prompt…"),
                Detach(_usbBar),
                Ui.Buttons(Ui.Button("Stop", () => _usbCts?.Cancel())))));
        }
        else
        {
            var refresh = Ui.Button("Look again", () => _ = ListDisksAsync());
            refresh.IsEnabled = !_listing;
            panel.Children.Add(Ui.Buttons(refresh));
            if (_listing) panel.Children.Add(Busy("Looking for USB sticks…"));
            else if (_disksError is { } error) panel.Children.Add(Note(Tone.Error, error, "Couldn't list the drives"));
            else if (_disks is { } disks)
            {
                var system = RecoveryUsb.SystemDriveLetter();
                var eligible = RecoveryUsb.Eligible(disks, system);
                if (eligible.Count == 0)
                    panel.Children.Add(Note(Tone.Warning, "No USB stick found. Plug one in (16 GB or larger) and press Look again."));
                foreach (var disk in eligible)
                {
                    var chosen = _chosenDisk is { } c && c.Number == disk.Number && c.Size == disk.Size;
                    var labels = disk.Labels.Count > 0 ? $" — currently named {string.Join(", ", disk.Labels)}" : "";
                    var detail = $"USB drive{labels}" + (disk.Size < RecoveryUsb.RecommendedBytes ? ". NVIDIA recommends 16 GB or more; this may be too small." : ".");
                    panel.Children.Add(Choice(Icons.Download, disk.Describe(), detail, chosen, () =>
                    {
                        _chosenDisk = disk;
                        _eraseConfirmed = false;
                        _host.Render();
                    }));
                }
                var skipped = disks.Where(d => d.IsUsb && RecoveryUsb.Ineligible(d, system) is not null).ToList();
                if (skipped.Count > 0)
                {
                    var reasons = new StackPanel();
                    foreach (var disk in skipped)
                        reasons.Children.Add(Ui.Secondary($"{disk.Describe()}: {RecoveryUsb.Ineligible(disk, system)}"));
                    panel.Children.Add(new Expander { Header = Ui.Text("Why isn't my drive listed?", 12.5), Content = reasons, Margin = new Thickness(0, 0, 0, 8) });
                }
            }

            if (_chosenDisk is { } target)
            {
                var confirm = Ui.Check($"I understand: erase {target.Describe()}", _eraseConfirmed, value =>
                {
                    _eraseConfirmed = value;
                    _host.Render();
                });
                confirm.Margin = new Thickness(0, 8, 0, 8);
                var write = Ui.Button("Erase it and make the recovery stick", () => _ = WriteUsbAsync(target), accent: true);
                write.IsEnabled = _eraseConfirmed && _source is not null;
                panel.Children.Add(Note(Tone.Warning, $"Everything on {target.Describe()} will be erased.", "Check it's the right stick", Ui.Stack(confirm, write)));
                if (_source is null)
                    panel.Children.Add(Note(Tone.Info, "Go back one step and choose the recovery file first."));
            }
            if (_usbStatus is { IsFailed: true } failed)
                panel.Children.Add(Note(Tone.Error, failed.Error ?? failed.Message, "The stick wasn't finished"));
        }

        panel.Children.Add(Aside("Windows asks for permission first (\"Do you want to allow this app to make changes?\") because erasing a drive needs administrator rights — only a small helper gets them, never the DSH window. Then it checks it's still the same stick, erases it, creates one FAT32 partition named BOOTME (at most 31 GB, the largest Windows formats as FAT32), marks it bootable, copies NVIDIA's files and adds EFI\\BOOT\\recovery.txt, the note that tells the Spark to start its installer."));
        panel.Children.Add(Stuck("If Windows pops up \"You need to format the disk\" when you plug the stick in, press Cancel. NVIDIA's own Windows script (CreateUSBKey) often fails with \"Set-Disk: Not Supported\" — this makes the same stick without it. Already have a working recovery stick? Press \"I already have a stick\"."));
        return panel;
    }

    private async Task WriteUsbAsync(UsbDisk disk)
    {
        if (_source is not { } source || !OperatingSystem.IsWindows()) return;
        if (!Dialog.Confirm($"Erase {disk.Describe()}?",
                $"Everything on {disk.Describe()} will be erased and replaced with the DGX Spark recovery files. This can't be undone.",
                "Erase and write", destructive: true))
            return;
        _writing = true;
        _usbDone = false;
        _usbStatus = null;
        _usbBar.Value = 0;
        _usbCts = new CancellationTokenSource();
        _host.Render();
        try
        {
            // Check once more from here, so a stick swapped since the list was made isn't even offered to the helper.
            var now = await RecoveryUsb.ListDisksAsync();
            var same = now.FirstOrDefault(d => d.Number == disk.Number && d.Size == disk.Size && d.SerialNumber == disk.SerialNumber);
            if (same is null || RecoveryUsb.Ineligible(same, RecoveryUsb.SystemDriveLetter()) is not null)
                throw new IOException("The USB stick changed since you chose it (or was unplugged). Nothing was erased — press Look again.");
            var folder = RecoveryUsbWriter.NewJobFolder();
            var job = new RecoveryUsbJob(disk.Number, disk.SerialNumber, disk.Size, disk.FriendlyName, source.Path, source.Root, source.TotalBytes,
                Path.Combine(folder, "status.json"), Path.Combine(folder, "cancel"));
            var final = await RecoveryUsbWriter.RunElevatedAsync(job, status => OnUi(() =>
            {
                _usbStatus = status;
                _usbBar.Value = status.Fraction;
                RenderIf(GuidePage.WriteUsb);
            }), _usbCts.Token);
            _usbStatus = final;
            _usbDone = final.IsDone;
        }
        catch (OperationCanceledException error)
        {
            _usbStatus = new RecoveryUsbStatus("failed", "Stopped", error.Message, Error: error.Message.Length > 0 && error.Message != "The operation was canceled." ? error.Message : "Stopped. Nothing more will be written.");
        }
        catch (Exception error)
        {
            _usbStatus = new RecoveryUsbStatus("failed", "Failed", error.Message, Error: error.Message);
        }
        finally
        {
            _writing = false;
            _usbCts = null;
            RenderIf(GuidePage.WriteUsb);
        }
    }

    // MARK: - Boot from it

    private UIElement BootUsbPage() => Ui.Stack(
        Picture("boot-menu", "A screen showing the Spark's boot menu with \"Install DGX OS\" selected, and a keyboard with Esc and Del highlighted."),
        Heading("Start the Spark from the stick"),
        Steps(
            "Unplug the Spark's power. Plug a **keyboard** and a **screen** into it, and unplug any other USB drives.",
            "Plug in the **recovery stick**.",
            "Plug the power back in and straight away **tap Esc or Del** a few times, until a setup screen appears.",
            "In that screen, choose the **USB stick** as the device to start from.",
            "In the menu \"DGX Spark Installation Options\", choose **Install DGX OS … for DGX Spark** and press Enter.",
            "Wait **25–30 minutes**. When it restarts by itself, unplug the stick."),
        Ui.Buttons(Ui.Button("Open NVIDIA's recovery instructions", () => ShellIntegration.Open(RecoveryDocs))),
        Aside("The Spark starts NVIDIA's installer from the stick. It erases the Spark's drive and writes a fresh DGX OS. Afterwards the Spark greets you exactly like a new one — so the next pages are its first-time setup."),
        Stuck("Nothing on the screen? Try its other video input and tap Esc or Del sooner after plugging in the power. Went straight into the old system? Unplug the power and try again. No USB option in the list? Check the stick is plugged in firmly, then remake it."));
}
