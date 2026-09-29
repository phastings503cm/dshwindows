namespace Dsh.Core.Tests;

/// <summary>Short-term Bedrock API keys, checked against aws-bedrock-token-generator 1.1.0 (its
/// _generate_token with botocore's clock frozen at 2026-09-29T12:34:56Z and the SigV4Tests credentials).</summary>
public sealed class BedrockApiKeysTests
{
    /// <summary>provide_token(region="us-west-2", expiry=12 h) without a session token.</summary>
    private const string TwelveHoursUsWest2 =
        "bedrock-api-key-YmVkcm9jay5hbWF6b25hd3MuY29tLz9BY3Rpb249Q2FsbFdpdGhCZWFyZXJUb2tlbiZYLUFtei1BbGdvcml0aG09QVdTNC1ITUFD" +
        "LVNIQTI1NiZYLUFtei1DcmVkZW50aWFsPUFLSURFWEFNUExFJTJGMjAyNjA5MjklMkZ1cy13ZXN0LTIlMkZiZWRyb2NrJTJGYXdzNF9yZXF1ZXN0JlgtQW16" +
        "LURhdGU9MjAyNjA5MjlUMTIzNDU2WiZYLUFtei1FeHBpcmVzPTQzMjAwJlgtQW16LVNpZ25lZEhlYWRlcnM9aG9zdCZYLUFtei1TaWduYXR1cmU9ZTE3YWFk" +
        "ZjI2ZjUyMjk0NTJkZmI4MjdkZWU5NWUxYjllNmM0ZGNhNDFiZTYxNzEzZThkODFjZDlkMmMzZmVjNiZWZXJzaW9uPTE=";

    /// <summary>region="us-east-1", expiry=1 h, with a session token.</summary>
    private const string OneHourUsEast1WithToken =
        "bedrock-api-key-YmVkcm9jay5hbWF6b25hd3MuY29tLz9BY3Rpb249Q2FsbFdpdGhCZWFyZXJUb2tlbiZYLUFtei1BbGdvcml0aG09QVdTNC1ITUFD" +
        "LVNIQTI1NiZYLUFtei1DcmVkZW50aWFsPUFLSURFWEFNUExFJTJGMjAyNjA5MjklMkZ1cy1lYXN0LTElMkZiZWRyb2NrJTJGYXdzNF9yZXF1ZXN0JlgtQW16" +
        "LURhdGU9MjAyNjA5MjlUMTIzNDU2WiZYLUFtei1FeHBpcmVzPTM2MDAmWC1BbXotU2lnbmVkSGVhZGVycz1ob3N0JlgtQW16LVNlY3VyaXR5LVRva2VuPUZ3" +
        "b0daWEl2WVhkekVYQU1QTEUlMkYlMkYlMkJUT0tFTiUzRCUzRCZYLUFtei1TaWduYXR1cmU9ZGRkMWJkZjViNDMwNmVhYzI0YjNmNzhjN2ZjOGJhNGYzNmVj" +
        "YjE4MWY4ZWEzMWQ0ZjBiNDRhNjA3NmRmYWIwYyZWZXJzaW9uPTE=";

    [Fact]
    public void MatchesTheTokenGeneratorLibrary()
    {
        Assert.Equal(TwelveHoursUsWest2, BedrockApiKeys.Create(SigV4Tests.NoToken, "us-west-2", SigV4Tests.Now));
        Assert.Equal(OneHourUsEast1WithToken,
            BedrockApiKeys.Create(SigV4Tests.WithToken, "us-east-1", SigV4Tests.Now, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void DescribeReadsRegionAndExpiryBack()
    {
        var info = BedrockApiKeys.Describe(TwelveHoursUsWest2)!;
        Assert.Equal("us-west-2", info.Region);
        Assert.Equal(SigV4Tests.Now, info.Issued);
        Assert.Equal(SigV4Tests.Now.AddHours(12), info.Expires);
        Assert.Equal(SigV4Tests.Key, info.AccessKeyId);
        Assert.Equal(SigV4Tests.Now.AddHours(1), BedrockApiKeys.Describe(OneHourUsEast1WithToken)!.Expires);
    }

    [Fact]
    public void LifetimeIsCappedAtTwelveHoursAndTheCredentialsExpiry()
    {
        var longer = BedrockApiKeys.Create(SigV4Tests.NoToken, "us-west-2", SigV4Tests.Now, TimeSpan.FromDays(1));
        Assert.Equal(SigV4Tests.Now.AddHours(12), BedrockApiKeys.Describe(longer)!.Expires);

        var shortLived = SigV4Tests.WithToken with { Expiration = SigV4Tests.Now.AddMinutes(30).AddMilliseconds(500) };
        var key = BedrockApiKeys.Create(shortLived, "eu-central-1", SigV4Tests.Now);
        Assert.Equal(SigV4Tests.Now.AddMinutes(30), BedrockApiKeys.Describe(key)!.Expires);
        Assert.Equal("eu-central-1", BedrockApiKeys.Describe(key)!.Region);
    }

    [Fact]
    public void ExpiredCredentialsCannotMintAKey()
    {
        var expired = SigV4Tests.WithToken with { Expiration = SigV4Tests.Now.AddSeconds(-1) };
        Assert.Throws<InvalidOperationException>(() => BedrockApiKeys.Create(expired, "us-east-1", SigV4Tests.Now));
    }

    [Theory]
    [InlineData("ABSKQmVkcm9ja0FQSUtleS1leGFtcGxl")] // a long-term key
    [InlineData("bedrock-api-key-!!!")]
    [InlineData("bedrock-api-key-aGVsbG8=")] // base64 of "hello"
    public void OtherStringsAreNotDescribed(string key)
    {
        Assert.Null(BedrockApiKeys.Describe(key));
    }
}
