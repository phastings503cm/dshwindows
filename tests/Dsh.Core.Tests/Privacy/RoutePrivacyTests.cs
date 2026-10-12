namespace Dsh.Core.Tests;

public sealed class RoutePrivacyTests
{
    [Theory]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://127.0.0.1:1234/v1")]
    [InlineData("http://127.5.6.7:8000/v1")]
    [InlineData("http://[::1]:8000/v1")]
    [InlineData("http://10.0.0.5:8000/v1")]
    [InlineData("http://172.16.0.1/v1")]
    [InlineData("http://172.31.255.254/v1")]
    [InlineData("https://192.168.1.42:11443/v1")]
    [InlineData("http://169.254.10.20/v1")]                // link-local
    [InlineData("http://100.64.0.1:8000/v1")]              // CGNAT (Tailscale)
    [InlineData("http://100.127.255.254:8000/v1")]
    [InlineData("http://[fd7a:115c:a1e0::1]:8000/v1")]     // IPv6 unique local (Tailscale)
    [InlineData("http://[fe80::1]:8000/v1")]               // IPv6 link-local
    [InlineData("http://[::ffff:192.168.1.5]:8000/v1")]    // an IPv4 address written as IPv6
    [InlineData("http://spark:8000/v1")]                   // a single-label name
    [InlineData("https://evo-x2:11443/v1")]
    [InlineData("http://spark-3f2a.local:8000/v1")]
    [InlineData("http://box.lan/v1")]
    [InlineData("http://box.home/v1")]
    [InlineData("http://box.home.arpa/v1")]
    [InlineData("http://gpu.internal/v1")]
    [InlineData("http://spark.tail1234.ts.net:8000/v1")]
    [InlineData("HTTP://SPARK.LOCAL/v1")]
    public void YourOwnMachinesArePrivateByDefault(string url)
    {
        Assert.True(RoutePrivacy.IsPrivateAddress(url));
        Assert.True(new ProviderProfile(ProviderKind.OpenAICompat, "r", url, "m").IsPrivateRoute);
    }

    [Theory]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://bedrock-runtime.us-east-1.amazonaws.com")]
    [InlineData("https://api.z.ai/api/coding/paas/v4")]
    [InlineData("https://api.kimi.ai/coding/v1")]
    [InlineData("https://coding-intl.dashscope.aliyuncs.com/v1")]
    [InlineData("https://my-gpu-box.example.com/v1")]       // an unknown name is someone else's machine
    [InlineData("http://8.8.8.8/v1")]
    [InlineData("http://172.32.0.1/v1")]                    // just outside RFC 1918
    [InlineData("http://100.128.0.1/v1")]                   // just outside CGNAT
    [InlineData("http://[2001:db8::1]/v1")]
    [InlineData("http://local.example.com/v1")]             // "local" not at the end
    [InlineData("not a url")]
    [InlineData("")]
    public void EverythingElseIsNot(string url) => Assert.False(RoutePrivacy.IsPrivateAddress(url));

    [Fact]
    public void AnExplicitChoiceWinsEitherWay()
    {
        var hosted = new ProviderProfile(ProviderKind.OpenAI, "OpenAI", "https://api.openai.com/v1", "gpt-5") { IsPrivate = true };
        Assert.True(hosted.IsPrivateRoute);
        var spark = new ProviderProfile(ProviderKind.OpenAICompat, "Spark", "https://192.168.1.42:11443/v1", "q") { IsPrivate = false };
        Assert.False(spark.IsPrivateRoute);
        var bedrock = new ProviderProfile(ProviderKind.Bedrock, "Bedrock", "https://bedrock-runtime.us-east-1.amazonaws.com", "claude");
        Assert.False(bedrock.IsPrivateRoute);
    }

    [Fact]
    public void OlderSettingsLoadUnchangedAndTheNewFieldsDefaultSafely()
    {
        var json = """{"kind":"openAICompat","name":"Spark","baseUrl":"http://192.168.1.42:8002/v1","model":"q","subagentWorker":true}""";
        var options = new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase };
        var route = System.Text.Json.JsonSerializer.Deserialize<ProviderProfile>(json, options)!;
        Assert.True(route.IsEnabled);
        Assert.False(route.IsBackup);
        Assert.Null(route.IsPrivate);
        Assert.True(route.IsPrivateRoute);
        Assert.False(route.IsInteractiveOnly);
        Assert.True(route.SubagentWorker);
        var written = System.Text.Json.JsonSerializer.Serialize(route with { Enabled = false, Backup = true, IsPrivate = false, InteractiveOnly = true }, options);
        Assert.Contains("\"enabled\":false", written);
        Assert.Contains("\"backup\":true", written);
        Assert.Contains("\"isPrivate\":false", written);
        Assert.Contains("\"interactiveOnly\":true", written);
    }
}
