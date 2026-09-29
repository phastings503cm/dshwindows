using System.Net.Http.Headers;
using System.Text;

namespace Dsh.Core.Tests;

/// <summary>SigV4 signatures checked against botocore 1.43.104 (SigV4Auth / SigV4QueryAuth with the
/// clock frozen at 2026-09-29T12:34:56Z and the credentials below). The expected values were printed
/// by a reference script run once; nothing here needs Python.</summary>
public sealed class SigV4Tests
{
    public static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 34, 56, TimeSpan.Zero);
    public const string Key = "AKIDEXAMPLE";
    public const string Secret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    public const string Token = "FwoGZXIvYXdzEXAMPLE//+TOKEN==";
    public static readonly AwsCredentials WithToken = new(Key, Secret, Token);
    public static readonly AwsCredentials NoToken = new(Key, Secret);

    public const string ModelId = "us.anthropic.claude-sonnet-4-5-20250929-v1:0";
    public const string ProfileArn = "arn:aws:bedrock:us-west-2:123456789012:inference-profile/us.anthropic.claude-opus-4-1-20250805-v1:0";
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"messages":[{"role":"user","content":[{"text":"hi"}]}]}""");

    private static HttpRequestMessage Post(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(Body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return request;
    }

    private static string Header(HttpRequestMessage request, string name) => string.Join(",", request.Headers.GetValues(name));

    [Fact]
    public void ModelIdsAreEncodedLikeBotocoreEncodesThePathParameter()
    {
        Assert.Equal("us.anthropic.claude-sonnet-4-5-20250929-v1%3A0", SigV4.Encode(ModelId));
        Assert.Equal("arn%3Aaws%3Abedrock%3Aus-west-2%3A123456789012%3Ainference-profile%2Fus.anthropic.claude-opus-4-1-20250805-v1%3A0",
            SigV4.Encode(ProfileArn));
        Assert.Equal("abc%2Bdef%2Fghi%3D%20x%C3%A9~", SigV4.Encode("abc+def/ghi= xé~"));
    }

    [Fact]
    public void ConverseStreamWithSessionTokenMatchesBotocore()
    {
        using var request = Post($"https://bedrock-runtime.us-east-1.amazonaws.com/model/{SigV4.Encode(ModelId)}/converse-stream");
        SigV4.Sign(request, Body, WithToken, "us-east-1", "bedrock", Now);

        // The URL keeps the encoded ':' on the wire.
        Assert.Equal("https://bedrock-runtime.us-east-1.amazonaws.com/model/us.anthropic.claude-sonnet-4-5-20250929-v1%3A0/converse-stream",
            request.RequestUri!.AbsoluteUri);
        Assert.Equal("20260929T123456Z", Header(request, "X-Amz-Date"));
        Assert.Equal(Token, Header(request, "X-Amz-Security-Token"));
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20260929/us-east-1/bedrock/aws4_request, " +
                     "SignedHeaders=content-type;host;x-amz-date;x-amz-security-token, " +
                     "Signature=2e93ac76b3d7dcfcd749068ad7e6548106763d0842edfed57350e2a2e0fe5b60",
            request.Headers.Authorization!.ToString());
    }

    [Fact]
    public void CanonicalRequestDoublyEncodesThePath()
    {
        var url = new Uri($"https://bedrock-runtime.us-east-1.amazonaws.com/model/{SigV4.Encode(ModelId)}/converse-stream");
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["content-type"] = "application/json",
            ["host"] = "bedrock-runtime.us-east-1.amazonaws.com",
            ["x-amz-date"] = "20260929T123456Z",
            ["x-amz-security-token"] = Token,
        };
        var canonical = SigV4.CanonicalRequest("POST", url, "", headers,
            "c3335e07d6e89650bf94cb98664be69f8774dbc3198df221f113db0a59ea14f0");
        Assert.Equal(
            "POST\n" +
            "/model/us.anthropic.claude-sonnet-4-5-20250929-v1%253A0/converse-stream\n" +
            "\n" +
            "content-type:application/json\n" +
            "host:bedrock-runtime.us-east-1.amazonaws.com\n" +
            "x-amz-date:20260929T123456Z\n" +
            "x-amz-security-token:FwoGZXIvYXdzEXAMPLE//+TOKEN==\n" +
            "\n" +
            "content-type;host;x-amz-date;x-amz-security-token\n" +
            "c3335e07d6e89650bf94cb98664be69f8774dbc3198df221f113db0a59ea14f0",
            canonical);
    }

    [Fact]
    public void InferenceProfileArnWithoutTokenMatchesBotocore()
    {
        using var request = Post($"https://bedrock-runtime.us-west-2.amazonaws.com/model/{SigV4.Encode(ProfileArn)}/converse-stream");
        SigV4.Sign(request, Body, NoToken, "us-west-2", "bedrock", Now);

        Assert.Contains("arn%3Aaws%3Abedrock%3Aus-west-2%3A123456789012%3Ainference-profile%2Fus.anthropic", request.RequestUri!.AbsoluteUri);
        Assert.False(request.Headers.Contains("X-Amz-Security-Token"));
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20260929/us-west-2/bedrock/aws4_request, " +
                     "SignedHeaders=content-type;host;x-amz-date, " +
                     "Signature=e6b4fe45fead2b5035469cf75d461f65bbc8faff141304b54cf936bd8794875e",
            request.Headers.Authorization!.ToString());
    }

    [Fact]
    public void GetWithQueryMatchesBotocore()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://bedrock.us-east-1.amazonaws.com/foundation-models?byOutputModality=TEXT");
        SigV4.Sign(request, [], WithToken, "us-east-1", "bedrock", Now);
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20260929/us-east-1/bedrock/aws4_request, " +
                     "SignedHeaders=host;x-amz-date;x-amz-security-token, " +
                     "Signature=59f7d182d2a9b1997881516e65ef9d1677434f925edd898cc3884acffaa467f8",
            request.Headers.Authorization!.ToString());
    }

    [Fact]
    public void EncodedPaginationTokenMatchesBotocore()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://bedrock.eu-central-1.amazonaws.com/inference-profiles?maxResults=1000&nextToken=" + SigV4.Encode("abc+def/ghi= x"));
        SigV4.Sign(request, [], NoToken, "eu-central-1", "bedrock", Now);
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20260929/eu-central-1/bedrock/aws4_request, " +
                     "SignedHeaders=host;x-amz-date, " +
                     "Signature=fdad7d3772854acef01ccb3e517a3e88afc5768e8e5d44330151cd16eba26f32",
            request.Headers.Authorization!.ToString());
    }

    [Fact]
    public void SigningAgainReplacesTheEarlierSignature()
    {
        using var request = Post($"https://bedrock-runtime.us-east-1.amazonaws.com/model/{SigV4.Encode(ModelId)}/converse-stream");
        SigV4.Sign(request, Body, new AwsCredentials("OLDKEY", "old", "old-token"), "us-east-1", "bedrock", Now.AddMinutes(-3));
        SigV4.Sign(request, Body, WithToken, "us-east-1", "bedrock", Now);
        Assert.Single(request.Headers.GetValues("X-Amz-Date"));
        Assert.Equal(Token, Header(request, "X-Amz-Security-Token"));
        Assert.EndsWith("Signature=2e93ac76b3d7dcfcd749068ad7e6548106763d0842edfed57350e2a2e0fe5b60",
            request.Headers.Authorization!.ToString());
    }

    [Fact]
    public void ContentSha256HeaderIsSentAndSigned()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.amazonaws.com/");
        SigV4.Sign(request, [], NoToken, "us-east-1", "s3", Now, contentSha256: true);
        Assert.Equal(SigV4.EmptyPayloadHash, Header(request, "X-Amz-Content-SHA256"));
        Assert.Contains("SignedHeaders=host;x-amz-content-sha256;x-amz-date,", request.Headers.Authorization!.ToString());
    }

    [Fact]
    public void CanonicalPathDropsDotAndEmptySegments()
    {
        Assert.Equal("/", SigV4.CanonicalPath(""));
        Assert.Equal("/", SigV4.CanonicalPath("/"));
        Assert.Equal("/a/c/d/", SigV4.CanonicalPath("/a/./b/../c//d/"));
        Assert.Equal("/model/x%253A0", SigV4.CanonicalPath("/model/x%3A0"));
    }

    [Fact]
    public void QueryIsSortedByEncodedKeyThenValue()
    {
        Assert.Equal("a=1&a=2&b=x%20y&c=",
            SigV4.CanonicalQuery(SigV4.Parameters(new Uri("https://h/?c=&b=x%20y&a=2&a=1"))));
    }

    [Fact]
    public void PresignMatchesBotocoreQueryAuth()
    {
        var url = SigV4.Presign(HttpMethod.Post, new Uri("https://bedrock.amazonaws.com/?Action=CallWithBearerToken"),
            WithToken, "us-east-1", "bedrock", Now, TimeSpan.FromHours(1));
        Assert.Equal("https://bedrock.amazonaws.com/?Action=CallWithBearerToken&X-Amz-Algorithm=AWS4-HMAC-SHA256" +
                     "&X-Amz-Credential=AKIDEXAMPLE%2F20260929%2Fus-east-1%2Fbedrock%2Faws4_request&X-Amz-Date=20260929T123456Z" +
                     "&X-Amz-Expires=3600&X-Amz-SignedHeaders=host&X-Amz-Security-Token=FwoGZXIvYXdzEXAMPLE%2F%2F%2BTOKEN%3D%3D" +
                     "&X-Amz-Signature=ddd1bdf5b4306eac24b3f78c7fc8ba4f36ecb181f8ea31d4f0b44a6076dfab0c",
            url);
    }

    [Fact]
    public void PresignRefusesImpossibleLifetimes()
    {
        var url = new Uri("https://bedrock.amazonaws.com/");
        Assert.Throws<ArgumentOutOfRangeException>(() => SigV4.Presign(HttpMethod.Get, url, NoToken, "us-east-1", "bedrock", Now, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => SigV4.Presign(HttpMethod.Get, url, NoToken, "us-east-1", "bedrock", Now, TimeSpan.FromDays(8)));
    }
}
