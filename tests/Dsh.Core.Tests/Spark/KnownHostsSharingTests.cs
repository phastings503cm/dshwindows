namespace Dsh.Core.Tests;

/// <summary>Trusted host keys are shared by every part of the app that connects over SSH (the setup guide, the OpenClaw
/// import). A pin one of them adds must survive another one saving, or a changed key would pass for a new host.</summary>
public sealed class KnownHostsSharingTests
{
    private static SshHostKey Key(string host, string fingerprint) => new(host, 22, "ssh-ed25519", fingerprint);

    [Fact]
    public void APinAnotherInstanceAddedSurvivesThisOneSaving()
    {
        using var temp = new TempDirectory();
        var path = temp["known_hosts.json"];
        var guide = new KnownHosts(path);
        guide.Remember(Key("a.local", "SHA256:aaa"));
        var import = new KnownHosts(path);

        import.Remember(Key("b.local", "SHA256:bbb")); // the import pins another host...
        guide.Remember(Key("c.local", "SHA256:ccc")); // ...and the guide, which had loaded earlier, saves again

        var fresh = new KnownHosts(path);
        Assert.Equal("SHA256:aaa", fresh.Lookup("a.local", 22)?.Fingerprint);
        Assert.Equal("SHA256:bbb", fresh.Lookup("b.local", 22)?.Fingerprint);
        Assert.Equal("SHA256:ccc", fresh.Lookup("c.local", 22)?.Fingerprint);
        // And the guide sees the import's pin too, so a changed key on b.local would be refused there.
        Assert.Equal("SHA256:bbb", guide.Lookup("b.local", 22)?.Fingerprint);
    }

    [Fact]
    public void APinIsSeenEvenWhenTheFilesTimestampDidNotChange()
    {
        // Two saves inside one clock tick carry the same timestamp; a reader that trusted it would miss the second.
        using var temp = new TempDirectory();
        var path = temp["known_hosts.json"];
        var guide = new KnownHosts(path);
        guide.Remember(Key("a.local", "SHA256:aaa"));
        var stamp = File.GetLastWriteTimeUtc(path);
        var import = new KnownHosts(path);

        import.Remember(Key("b.local", "SHA256:bbb"));
        File.SetLastWriteTimeUtc(path, stamp); // as if the disk's clock had not moved
        guide.Remember(Key("c.local", "SHA256:ccc"));

        var fresh = new KnownHosts(path);
        Assert.Equal("SHA256:aaa", fresh.Lookup("a.local", 22)?.Fingerprint);
        Assert.Equal("SHA256:bbb", fresh.Lookup("b.local", 22)?.Fingerprint);
        Assert.Equal("SHA256:ccc", fresh.Lookup("c.local", 22)?.Fingerprint);
    }

    [Fact]
    public void ADamagedFileIsKeptAsACopyAndTheStoreStartsOver()
    {
        using var temp = new TempDirectory();
        var path = temp["known_hosts.json"];
        File.WriteAllText(path, "{ this is not json");
        var hosts = new KnownHosts(path);

        Assert.Null(hosts.Lookup("a.local", 22));
        hosts.Remember(Key("a.local", "SHA256:aaa"));

        Assert.Equal("{ this is not json", File.ReadAllText(path + ".damaged"));
        Assert.Equal("SHA256:aaa", new KnownHosts(path).Lookup("a.local", 22)?.Fingerprint);
    }

    [UnixFact]
    public void AFileThatCannotBeReadRightNowIsNotOverwrittenWithWhatIsInMemory()
    {
        using var temp = new TempDirectory();
        var path = temp["known_hosts.json"];
        var first = new KnownHosts(path);
        first.Remember(Key("a.local", "SHA256:aaa"));
        var original = File.ReadAllText(path);
        var second = new KnownHosts(path);

        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            if (CanRead(path)) return; // (a test run by someone the permissions don't stop — root, in a container — proves nothing)
            second.Remember(Key("b.local", "SHA256:bbb")); // can't read the pins that are there: must not replace them with just this one
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        Assert.Equal(original, File.ReadAllText(path));
    }

    private static bool CanRead(string path)
    {
        try
        {
            File.ReadAllBytes(path);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
