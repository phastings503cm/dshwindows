using System.Text;
using System.Text.Json;

namespace Dsh.Core.Tests;

/// <summary>New (SparkSwapperLiveTests.swift needs a real swapper and is not ported): decoding the
/// /api/status payload and the pure helpers the live test exercised.</summary>
public sealed class SwapperStatusTests
{
    private const string Payload = """
        {
          "active": "standard",
          "loading": null,
          "busy": false,
          "models": {
            "standard": {"key": "standard", "title": "Qwen3.8 27B", "tagline": "balanced", "engine": "sglang",
                         "context": 262144, "served_context": 1048576, "served_id": "qwen3.8-27b",
                         "vision": true, "running": true, "healthy": true, "order": 1},
            "flash":    {"key": "flash", "title": "Qwen3.8 Flash", "engine": "vllm", "context": 131072,
                         "served_id": "qwen3.8-flash", "running": false, "healthy": false, "order": 0},
            "coder":    {"key": "coder", "title": "Coder", "context": 65536, "served_id": "qwen3-coder-30b",
                         "running": false, "healthy": false}
          },
          "job": {"id": "j1", "target": "flash", "source": "standard", "state": "running", "error": null,
                  "note": null, "started": 1727550000.5, "finished": null,
                  "steps": [{"key": "stop", "label": "Stopping standard", "state": "done"},
                            {"key": "start", "label": "Starting flash", "state": "running"}]},
          "openclaw_primary": "standard"
        }
        """;

    private static SwapperStatus Decode(string json) =>
        JsonSerializer.Deserialize<SwapperStatus>(json, SwapperStatus.Json) ?? throw new InvalidOperationException("null");

    [Fact]
    public void DecodesSnakeCaseFields()
    {
        var status = Decode(Payload);

        Assert.Equal("standard", status.Active);
        Assert.Null(status.Loading);
        Assert.False(status.Busy);
        Assert.Equal("standard", status.OpenclawPrimary);
        Assert.Equal(3, status.AllModels.Count);

        var standard = status.AllModels["standard"];
        Assert.Equal("Qwen3.8 27B", standard.Title);
        Assert.Equal("balanced", standard.Tagline);
        Assert.Equal("sglang", standard.Engine);
        Assert.Equal(262_144, standard.Context);
        Assert.Equal(1_048_576, standard.ServedContext);
        Assert.Equal("qwen3.8-27b", standard.ServedId);
        Assert.True(standard.Vision);
        Assert.True(standard.Running);
        Assert.True(standard.Healthy);
        Assert.Equal(1, standard.Order);
        Assert.Equal("standard", standard.Id);

        var flash = status.AllModels["flash"];
        Assert.Null(flash.ServedContext);
        Assert.Null(flash.Tagline);
        Assert.Null(flash.Vision);

        var job = Assert.IsType<SwapperJob>(status.Job);
        Assert.Equal("flash", job.Target);
        Assert.Equal("standard", job.Source);
        Assert.Equal(1727550000.5, job.Started);
        Assert.Null(job.Finished);
        Assert.Equal(2, job.Steps?.Count);
    }

    [Fact]
    public void ActiveModelAndDisplayOrder()
    {
        var status = Decode(Payload);
        Assert.Equal("qwen3.8-27b", status.ActiveModel?.ServedId);
        // By order (missing sorts as 9), then title.
        Assert.Equal(["flash", "standard", "coder"], status.Ordered.Select(m => m.Key));
    }

    [Theory]
    [InlineData("flash", "flash")]          // a key
    [InlineData("  FLASH ", "flash")]       // trimmed, any case
    [InlineData("27b", "standard")]         // part of a served id
    [InlineData("qwen3.8 flash", "flash")]  // a title
    [InlineData("qwen3-coder-30b", "coder")] // a served id
    [InlineData("cod", "coder")]
    public void ResolveMatchesUserInput(string query, string expected)
    {
        Assert.Equal(expected, Decode(Payload).Resolve(query));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("qwen")]   // ambiguous: every model matches
    [InlineData("nope")]
    public void ResolveRefusesToGuess(string query)
    {
        Assert.Null(Decode(Payload).Resolve(query));
    }

    [Fact]
    public void SwitchingFollowsBusyAndTheJob()
    {
        Assert.True(Decode(Payload).IsSwitching); // a running job
        Assert.True(Decode("""{"busy":true}""").IsSwitching);
        Assert.False(Decode("""{"busy":false,"job":{"id":"j","target":"t","state":"done","started":1,"steps":[]}}""").IsSwitching);
        Assert.False(Decode("""{"busy":false}""").IsSwitching);
    }

    [Fact]
    public void CurrentStepIsTheRunningOneElseTheLastFailure()
    {
        Assert.Equal("start", Decode(Payload).Job?.CurrentStep?.Key);
        var failed = Decode("""
            {"busy":false,"job":{"id":"j","target":"t","state":"failed","started":1,"error":"boom",
             "steps":[{"key":"a","label":"A","state":"failed"},{"key":"b","label":"B","state":"failed"},{"key":"c","label":"C","state":"pending"}]}}
            """);
        Assert.Equal("b", failed.Job?.CurrentStep?.Key);
        Assert.Equal("boom", failed.Job?.Error);
        Assert.False(failed.Job?.IsRunning);
        Assert.Null(new SwapperJob("j", "t", null, "done", null, null, 1, null, null).CurrentStep);
    }

    [Fact]
    public void AnEmptyPayloadIsHarmless()
    {
        var status = Decode("{}");
        Assert.Empty(status.AllModels);
        Assert.Empty(status.Ordered);
        Assert.Null(status.ActiveModel);
        Assert.Null(status.Resolve("flash"));
        Assert.False(status.IsSwitching);
    }
}

/// <summary>New: SparkSwapperClient's pure helpers.</summary>
public sealed class SparkSwapperClientTests
{
    [Theory]
    [InlineData("http://192.168.1.10:8002/v1", "https://192.168.1.10:8999")]
    [InlineData("https://spark.local/v1", "https://spark.local:8999")]
    [InlineData("http://[::1]:8000/v1", "https://[::1]:8999")]
    public void DefaultUrlIsTheSameHostOnPort8999(string modelServer, string expected)
    {
        Assert.Equal(expected, SparkSwapperClient.DefaultUrl(modelServer));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    public void DefaultUrlNeedsAnAbsoluteUrl(string modelServer)
    {
        Assert.Null(SparkSwapperClient.DefaultUrl(modelServer));
    }

    [Fact]
    public void FingerprintIsColonSeparatedUppercaseSha256()
    {
        var fingerprint = SparkSwapperClient.Fingerprint(Encoding.ASCII.GetBytes("abc"));
        Assert.Equal("BA:78:16:BF:8F:01:CF:EA:41:41:40:DE:5D:AE:22:23:B0:03:61:A3:96:17:7A:9C:B4:10:FF:61:F2:00:15:AD", fingerprint);
        Assert.Equal(95, fingerprint.Length); // 32 bytes as openssl prints them
    }

    [Fact]
    public void UntrustedCertificateErrorShowsTheFingerprintPrefix()
    {
        var fingerprint = SparkSwapperClient.Fingerprint([1, 2, 3]);
        var error = SwapperException.UntrustedCertificate(fingerprint);
        Assert.Equal(SwapperErrorKind.UntrustedCertificate, error.Kind);
        Assert.Equal(fingerprint, error.Fingerprint);
        Assert.Contains(fingerprint[..23] + "…", error.Message);
        Assert.Equal(503, SwapperException.Server(503, "busy").StatusCode);
    }

    [Fact]
    public void ANewClientRemembersItsSettingsWithoutConnecting()
    {
        using var client = new SparkSwapperClient(new Uri("https://127.0.0.1:8999"), "user", "pass", "AB:CD");
        Assert.Equal("AB:CD", client.PinnedFingerprint);
        Assert.Null(client.SeenFingerprint);
        Assert.Equal(new Uri("https://127.0.0.1:8999"), client.BaseUrl);
    }
}
