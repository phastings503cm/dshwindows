namespace Vision.Core.Tests;

/// <summary>New: the DPAPI entropy the credentials vault and the shield's saved terms were encrypted with. These bytes
/// can never change — not even to drop the app's former name (DSH) from them — or every user's vault and terms become
/// unreadable. If this test fails, put the bytes back; don't update the test.</summary>
public sealed class FrozenEntropyTests
{
    [Fact]
    public void TheVaultEntropyIsFrozen() =>
        Assert.Equal("44534820666F722057696E646F777320C2B72063726564656E7469616C207661756C7420C2B7207631",
            Convert.ToHexString(FrozenEntropy.Vault));

    [Fact]
    public void ThePrivacyTermsEntropyIsFrozen() =>
        Assert.Equal("44534820666F722057696E646F777320C2B72070726976616379207465726D7320C2B7207631",
            Convert.ToHexString(FrozenEntropy.PrivacyTerms));

    [Fact]
    public void EachCallGetsItsOwnCopy()
    {
        var first = FrozenEntropy.Vault;
        first[0] = 0;
        Assert.Equal(0x44, FrozenEntropy.Vault[0]);
    }
}
