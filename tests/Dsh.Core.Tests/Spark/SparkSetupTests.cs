namespace Dsh.Core.Tests;

public sealed class SparkSetupTests
{
    [Theory]
    [InlineData("192.168.1.42", "192.168.1.42")]
    [InlineData("https://192.168.1.42:8999/", "192.168.1.42")]
    [InlineData("spark-3f2a.local:22", "spark-3f2a.local")]
    [InlineData("  spark-3f2a.local ", "spark-3f2a.local")]
    [InlineData("[fe80::1]", "fe80::1")]
    public void HostsFromWhatPeopleType(string typed, string host) => Assert.Equal(host, SparkSetup.HostOf(typed));

    [Fact]
    public void Addresses()
    {
        Assert.Equal(new Uri("https://192.168.1.42:8999"), SparkSetup.SwapperUrl("192.168.1.42"));
        Assert.Equal("https://192.168.1.42:11443/v1", SparkSetup.ModelBaseUrl("192.168.1.42"));
        Assert.Equal("https://[fe80::1]:11443/v1", SparkSetup.ModelBaseUrl("fe80::1"));
    }

    [Fact]
    public void TheRouteCarriesKeyAndPin()
    {
        var profile = SparkSetup.Provider("192.168.1.42", "qwen3.8-27b-sglang", "vllm-local", "AB:CD");
        Assert.Equal(ProviderKind.OpenAICompat, profile.Kind);
        Assert.Equal("DGX Spark", profile.Name);
        Assert.Equal("https://192.168.1.42:11443/v1", profile.BaseUrl);
        Assert.Equal("qwen3.8-27b-sglang", profile.Model);
        Assert.Equal("vllm-local", profile.ApiKey);
        Assert.Equal("AB:CD", profile.PinnedCertificate);
        Assert.True(profile.IsSelfHosted);
        Assert.Null(SparkSetup.Provider("h", "m", "", null).ApiKey);
    }

    [Fact]
    public async Task SaysHello()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Sse(
            """{"choices":[{"delta":{"content":"Hello and "}}]}""",
            """{"choices":[{"delta":{"content":"welcome!"},"finish_reason":"stop"}]}"""));
        var profile = SparkSetup.Provider("192.168.1.42", "qwen3.8-27b-sglang", "vllm-local", null);
        var reply = await SparkSetup.SayHelloAsync(new OpenAiClient(profile, handler), profile.Model);
        Assert.Equal("Hello and welcome!", reply);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://192.168.1.42:11443/v1/chat/completions", request.Url.ToString());
        Assert.Equal("Bearer vllm-local", request.Headers["Authorization"]);
        Assert.Contains("\"model\":\"qwen3.8-27b-sglang\"", request.Body);
    }
}
