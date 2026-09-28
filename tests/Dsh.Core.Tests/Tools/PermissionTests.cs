namespace Dsh.Core.Tests;

/// <summary>Ported from the PermissionTests class in HarnessTests.swift. The workspace need not exist:
/// the policy only does path arithmetic.</summary>
public sealed class PermissionTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "dsh-project");

    private static PermissionPolicy Policy(PermissionPreset preset) => new(preset, Root);

    /// <summary>A path outside the workspace on this OS (like the Swift tests' /etc/hosts).</summary>
    private static readonly string Outside = Path.Combine(Path.GetTempPath(), "somewhere-else", "hosts");

    [Fact]
    public void RelativePathsResolveInsideTheWorkspace()
    {
        var (path, inside) = Policy(PermissionPreset.WorkspaceWrite).Resolve("src/main.swift");
        Assert.Equal(Path.Combine(Root, "src", "main.swift"), path);
        Assert.True(inside);
    }

    [Fact]
    public void TraversalEscapesAreDetected()
    {
        Assert.False(Policy(PermissionPreset.WorkspaceWrite).Resolve("../../etc/passwd").Inside);
    }

    /// <summary>A sibling directory sharing a name prefix is *not* inside the project.</summary>
    [Fact]
    public void PrefixSiblingIsNotInside()
    {
        Assert.False(Policy(PermissionPreset.WorkspaceWrite).Resolve(Root + "-other" + Path.DirectorySeparatorChar + "file").Inside);
    }

    [Fact]
    public void WriteInsideProceedsAndOutsideAsks()
    {
        Assert.Equal(PermissionVerdict.Proceed, Policy(PermissionPreset.WorkspaceWrite).CheckWrite("notes.md").Verdict);
        Assert.Equal(PermissionVerdict.Ask, Policy(PermissionPreset.WorkspaceWrite).CheckWrite(Outside).Verdict);
    }

    [Fact]
    public void FullAccessWritesAnywhereWithoutAsking()
    {
        Assert.Equal(PermissionVerdict.Proceed, Policy(PermissionPreset.FullAccess).CheckWrite(Outside).Verdict);
    }

    /// <summary>Plan mode tells the user nothing will be modified, so the policy has to back that up
    /// even for a write inside the project.</summary>
    [Fact]
    public void PlanModeAsksBeforeWritingAnywhere()
    {
        Assert.Equal(PermissionVerdict.Ask, Policy(PermissionPreset.Plan).CheckWrite("notes.md").Verdict);
        Assert.Equal(PermissionVerdict.Ask, Policy(PermissionPreset.Plan).CheckWrite(Outside).Verdict);
    }

    [Fact]
    public void PlanModeAsksBeforeEveryCommand()
    {
        Assert.Equal(PermissionVerdict.Ask, Policy(PermissionPreset.Plan).CheckShell("ls").Verdict);
    }

    [Theory]
    [InlineData("rm -rf build")]
    [InlineData("git push origin main")]
    [InlineData("brew install jq")]
    [InlineData("echo x > out")]
    public void MutatingCommandsAskUnderWorkspaceWrite(string command)
    {
        Assert.Equal(PermissionVerdict.Ask, Policy(PermissionPreset.WorkspaceWrite).CheckShell(command).Verdict);
    }

    [Theory]
    [InlineData("ls -la")]
    [InlineData("git status")]
    [InlineData("swift build")]
    public void ReadOnlyCommandsRunWithoutAsking(string command)
    {
        Assert.Equal(PermissionVerdict.Proceed, Policy(PermissionPreset.WorkspaceWrite).CheckShell(command).Verdict);
    }

    [Fact]
    public void TildeExpansion()
    {
        Assert.Equal(PermissionPolicy.HomeDirectory, Policy(PermissionPreset.WorkspaceWrite).Expand("~"));
        Assert.Equal(Path.Join(PermissionPolicy.HomeDirectory, "x"), Policy(PermissionPreset.WorkspaceWrite).Expand("~/x"));
    }
}

/// <summary>New: path containment edge cases, and the Windows spellings (drive letters, backslashes,
/// Git Bash's /c/…) the port has to understand.</summary>
public sealed class PermissionPathTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "dsh-project");

    private static PermissionPolicy Policy() => new(PermissionPreset.WorkspaceWrite, Root);

    [Fact]
    public void AbsolutePathsInsideAndTheRootItself()
    {
        var policy = Policy();
        Assert.True(policy.Resolve(Path.Combine(Root, "a", "b.txt")).Inside);
        Assert.Equal((Root, true), policy.Resolve("."));
        Assert.Equal((Root, true), policy.Resolve(Root + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void DotDotThatStaysInsideIsInside()
    {
        Assert.Equal((Path.Combine(Root, "README.md"), true), Policy().Resolve("src/../README.md"));
        Assert.False(Policy().Resolve("src/../../escaped.txt").Inside);
    }

    [Fact]
    public void TildePathsResolveAgainstHomeNotTheWorkspace()
    {
        var (path, inside) = Policy().Resolve("~/notes.txt");
        Assert.Equal(Path.Join(PermissionPolicy.HomeDirectory, "notes.txt"), path);
        Assert.False(inside);
    }

    [Fact]
    public void QuotedPathsAreUnquoted()
    {
        Assert.Equal((Path.Combine(Root, "a b.txt"), true), Policy().Resolve("\"a b.txt\""));
    }

    [Fact]
    public void WorkspaceRootIsNormalizedWithoutTrailingSeparator()
    {
        var policy = new PermissionPolicy(PermissionPreset.WorkspaceWrite, Root + Path.DirectorySeparatorChar);
        Assert.Equal(Root, policy.WorkspaceRoot);
        Assert.True(policy.IsInside(Path.Combine(Root, "x")));
        Assert.False(policy.IsInside(Root + "x"));
    }

    [Fact]
    public void ComputerAccessIsNeverImplicitAndPlanModeNeverControls()
    {
        Assert.Equal(PermissionVerdict.Ask, Policy().CheckComputer(ComputerAccess.Observe).Verdict);
        Assert.Equal(PermissionVerdict.Ask, Policy().CheckComputer(ComputerAccess.Control).Verdict);
        var plan = new PermissionPolicy(PermissionPreset.Plan, Root);
        Assert.Equal(PermissionVerdict.Ask, plan.CheckComputer(ComputerAccess.Observe).Verdict);
        var denied = plan.CheckComputer(ComputerAccess.Control);
        Assert.Equal(PermissionVerdict.Deny, denied.Verdict);
        Assert.Contains("Plan mode", denied.Reason);
        Assert.Equal(PermissionVerdict.Proceed,
            new PermissionPolicy(PermissionPreset.FullAccess, Root).CheckComputer(ComputerAccess.Control).Verdict);
    }

    [Fact]
    public void PresetRawValuesRoundTrip()
    {
        foreach (var preset in PermissionPresets.All)
            Assert.Equal(preset, PermissionPresets.FromRaw(preset.RawValue()));
        Assert.Null(PermissionPresets.FromRaw("yolo"));
        Assert.Equal("workspaceWrite", PermissionPreset.WorkspaceWrite.RawValue());
    }

    private const string WinRoot = @"C:\Work\Proj";

    private static PermissionPolicy WinPolicy() => new(PermissionPreset.WorkspaceWrite, WinRoot);

    [WindowsFact]
    public void DriveLetterAndCaseDoNotMatterOnWindows()
    {
        Assert.True(WinPolicy().Resolve(@"c:\work\PROJ\src\x.cs").Inside);
        Assert.True(WinPolicy().Resolve(@"C:\WORK\proj").Inside);
        Assert.False(WinPolicy().Resolve(@"D:\Work\Proj\x.cs").Inside);
        Assert.False(WinPolicy().Resolve(@"c:\work\proj-other\x.cs").Inside);
    }

    [WindowsFact]
    public void BackslashesAndForwardSlashesBothWorkOnWindows()
    {
        Assert.Equal((@"C:\Work\Proj\src\x.cs", true), WinPolicy().Resolve(@"src\x.cs"));
        Assert.Equal((@"C:\Work\Proj\src\x.cs", true), WinPolicy().Resolve("src/x.cs"));
        Assert.Equal((@"C:\Work\Proj\x.cs", true), WinPolicy().Resolve(@"src\..\x.cs"));
        Assert.False(WinPolicy().Resolve(@"..\..\Windows\System32\drivers\etc\hosts").Inside);
    }

    [WindowsFact]
    public void MsysDriveSpellingIsUnderstoodOnWindows()
    {
        Assert.Equal((@"C:\Work\Proj\src\x.cs", true), WinPolicy().Resolve("/c/Work/Proj/src/x.cs"));
        Assert.Equal(@"D:\data", WinPolicy().Expand("/d/data"));
        Assert.Equal(@"C:\", WinPolicy().Expand("/c"));
    }

    [WindowsFact]
    public void RootedPathWithoutADriveUsesTheWorkspaceDrive()
    {
        Assert.Equal((@"C:\src\x.cs", false), WinPolicy().Resolve(@"\src\x.cs"));
    }

    [WindowsFact]
    public void TildeWithBackslashExpandsOnWindows()
    {
        Assert.Equal(Path.Join(PermissionPolicy.HomeDirectory, "x"), WinPolicy().Expand(@"~\x"));
    }

    [UnixFact]
    public void CaseMattersOnUnix()
    {
        Assert.False(Policy().Resolve(Root.ToUpperInvariant() + "/x").Inside);
    }
}

/// <summary>New: the PowerShell / cmd.exe / package-manager vocabulary behind LooksMutating.</summary>
public sealed class ShellMutationTests
{
    [Theory]
    [InlineData("Remove-Item -Recurse -Force build")]
    [InlineData("remove-item x.txt")]
    [InlineData("Get-ChildItem *.tmp | Remove-Item")]
    [InlineData("Set-Content -Path a.txt -Value hi")]
    [InlineData("Add-Content log.txt 'x'")]
    [InlineData("New-Item -ItemType Directory out")]
    [InlineData("Copy-Item a b")]
    [InlineData("Move-Item a b")]
    [InlineData("Rename-Item a b")]
    [InlineData("'x' | Out-File a.txt")]
    [InlineData("Stop-Process -Name notepad")]
    [InlineData("Start-Process setup.exe")]
    [InlineData("Set-ItemProperty HKCU:\\Software\\X -Name a -Value 1")]
    [InlineData("Install-Module PSReadLine")]
    [InlineData("Expand-Archive a.zip out")]
    [InlineData("del foo.txt")]
    [InlineData("erase /q foo.txt")]
    [InlineData("rmdir /s /q build")]
    [InlineData("rd build")]
    [InlineData("md newdir")]
    [InlineData("copy a.txt b.txt")]
    [InlineData("xcopy /E src dst")]
    [InlineData("robocopy src dst /MIR")]
    [InlineData("ren a.txt b.txt")]
    [InlineData("cd src && del *.obj")]
    [InlineData("taskkill /F /IM app.exe")]
    [InlineData("icacls x /grant Everyone:F")]
    [InlineData("mklink /D link target")]
    [InlineData("setx PATH C:\\bin")]
    [InlineData("reg add HKCU\\Software\\X /v a /d 1")]
    [InlineData("reg.exe delete HKCU\\Software\\X /f")]
    [InlineData("winget install Git.Git")]
    [InlineData("choco install nodejs")]
    [InlineData("scoop uninstall jq")]
    [InlineData("npm install")]
    [InlineData("npm i lodash")]
    [InlineData("pnpm add react")]
    [InlineData("yarn remove left-pad")]
    [InlineData("pip install requests")]
    [InlineData("dotnet add package Newtonsoft.Json")]
    [InlineData("dotnet tool install -g dotnet-ef")]
    [InlineData("git push")]
    [InlineData("git commit -m wip")]
    [InlineData("git clean -fdx")]
    [InlineData("git stash")]
    [InlineData("git branch -D feature")]
    [InlineData("GIT RESET --hard")]
    [InlineData("echo hi > out.txt")]
    public void MutatingCommandsAreFlagged(string command)
    {
        Assert.True(PermissionPolicy.LooksMutating(command));
    }

    [Theory]
    [InlineData("Get-ChildItem -Recurse")]
    [InlineData("gci")]
    [InlineData("Get-Content README.md")]
    [InlineData("Select-String -Pattern TODO -Path *.cs")]
    [InlineData("Get-Process | Select-Object Name")]
    [InlineData("Test-Path src")]
    [InlineData("dir")]
    [InlineData("dir /s /b")]
    [InlineData("cmd /c dir")]
    [InlineData("type README.md")]
    [InlineData("cat README.md")]
    [InlineData("where.exe git")]
    [InlineData("findstr /s TODO *.cs")]
    [InlineData("git status")]
    [InlineData("git log --oneline -5")]
    [InlineData("git diff HEAD~1")]
    [InlineData("git branch")]
    [InlineData("dotnet build")]
    [InlineData("dotnet test --no-build")]
    [InlineData("npm run lint")]
    [InlineData("python -c \"print(1)\"")]
    public void ReadOnlyCommandsAreNotFlagged(string command)
    {
        Assert.False(PermissionPolicy.LooksMutating(command));
    }
}
