using System.Text.Json.Nodes;

namespace Dsh.Core.Tests;

/// <summary>Ported from ContextWindowTests.swift.</summary>
public sealed class ContextWindowTests
{
    [Fact]
    public void ExactOllamaTags()
    {
        Assert.Equal(32_768, FallbackContextWindow.Limit("qwen3:8b"));
        Assert.Equal(131_072, FallbackContextWindow.Limit("llama3.1:70b"));
    }

    [Fact]
    public void VendorPrefixesStripDown()
    {
        // "meta-llama/Llama-3.3-70B-Instruct" → base "Llama-3.3-70B-Instruct" → lowercased → prefix
        // "llama-3.3-" → 128k.
        Assert.Equal(131_072, FallbackContextWindow.Limit("meta-llama/Llama-3.3-70B-Instruct"));
        Assert.Equal(128_000, FallbackContextWindow.Limit("openai/gpt-4o"));
        Assert.Equal(128_000, FallbackContextWindow.Limit("gpt-4o-mini"));
    }

    [Fact]
    public void UnknownModelReturnsNull()
    {
        Assert.Null(FallbackContextWindow.Limit("my-custom-fine-tune-xyz"));
    }

    [Fact]
    public void OllamaTagStrippedForPrefixMatch()
    {
        Assert.Equal(65_536, FallbackContextWindow.Limit("deepseek-r1:32b"));
        // A tag not in the exact table still matches the family prefix.
        Assert.Equal(32_768, FallbackContextWindow.Limit("qwen3:60b"));
    }

    [Fact]
    public void MatchesModelToleratesVendorAndTags()
    {
        Assert.True(OpenAiClient.MatchesModel("qwen3:8b", "qwen3:8b"));
        Assert.True(OpenAiClient.MatchesModel("meta-llama/Llama-3.3-70B-Instruct", "Llama-3.3-70B-Instruct"));
        Assert.True(OpenAiClient.MatchesModel("Llama-3.3-70B-Instruct", "meta-llama/Llama-3.3-70B-Instruct"));
        Assert.False(OpenAiClient.MatchesModel("gpt-4o", "gpt-4o-mini"));
    }

    [Fact]
    public void ContextWindowFieldExtraction()
    {
        Assert.Equal(131_072, OpenAiClient.ContextWindow(new JsonObject { ["context"] = 131_072 }));
        Assert.Equal(32_768, OpenAiClient.ContextWindow(new JsonObject { ["context_length"] = 32_768 }));
        Assert.Equal(128_000, OpenAiClient.ContextWindow(new JsonObject { ["max_context_length"] = 128_000 }));
        Assert.Equal(200_000, OpenAiClient.ContextWindow(new JsonObject { ["context_window"] = 200_000 }));
        // SGLang's /v1/models reports max_model_len when the model is served behind a proxy that only
        // forwards the /v1/* surface.
        Assert.Equal(1_000_000, OpenAiClient.ContextWindow(new JsonObject { ["max_model_len"] = 1_000_000 }));
        Assert.Null(OpenAiClient.ContextWindow(new JsonObject { ["max_total_tokens"] = 262_144 })); // KV pool, not a window
        Assert.Null(OpenAiClient.ContextWindow(new JsonObject { ["id"] = "gpt-4o" }));
        // Zero / negative are ignored, so a bogus 0 doesn't win.
        Assert.Null(OpenAiClient.ContextWindow(new JsonObject { ["context"] = 0 }));
    }
}
