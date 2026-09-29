using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using static Dsh.Core.Tests.EventStreamFrames;

namespace Dsh.Core.Tests;

/// <summary>BedrockClient against an in-memory Bedrock: real event-stream frames, AWS error replies,
/// credential refresh, API keys, endpoints, model listing, and the engine round trip of Claude's signed
/// reasoning.</summary>
public sealed class BedrockClientTests
{
    private const string Sonnet45 = "us.anthropic.claude-sonnet-4-5-20250929-v1:0";
    private const string Opus47 = "us.anthropic.claude-opus-4-7";
    private const string Runtime = "https://bedrock-runtime.us-east-1.amazonaws.com";

    private static ProviderProfile Profile(string model = Sonnet45) =>
        new(ProviderKind.Bedrock, "Bedrock", Runtime, model) { AwsProfile = "dsh-bedrock", AwsRegion = "us-east-1" };

    private static BedrockClient Client(FakeHttpHandler handler, IAwsCredentialSource? source = null, ProviderProfile? profile = null) =>
        new(profile ?? Profile(), source ?? new StaticAwsCredentialSource(SigV4Tests.WithToken), handler, new ManualClock(SigV4Tests.Now));

    private static LlmRequest Request(params LlmMessage[] messages) =>
        new("You are DSH.", messages.Length == 0 ? [LlmMessage.User("hi")] : messages, [], "");

    private sealed record Collected(string Text, string Reasoning, LlmStreamEvent.Done Done);

    private static async Task<Collected> CollectAsync(ILlmClient client, LlmRequest? request = null, CancellationToken cancellationToken = default)
    {
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        LlmStreamEvent.Done? done = null;
        await foreach (var ev in client.StreamAsync(request ?? Request(), cancellationToken))
        {
            switch (ev)
            {
                case LlmStreamEvent.Text t: text.Append(t.Delta); break;
                case LlmStreamEvent.Reasoning r: reasoning.Append(r.Delta); break;
                case LlmStreamEvent.Done d: done = d; break;
            }
        }
        return new Collected(text.ToString(), reasoning.ToString(), done!);
    }

    /// <summary>Hands out credentials in turn: the next set each time a refresh is forced.</summary>
    private sealed class RotatingSource(params AwsCredentials[] sets) : IAwsCredentialSource
    {
        private readonly Lock _lock = new();
        private readonly List<bool> _calls = [];
        private int _index;

        public IReadOnlyList<bool> Calls
        {
            get
            {
                lock (_lock) return [.. _calls];
            }
        }

        public Task<AwsCredentials> GetAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                _calls.Add(forceRefresh);
                if (forceRefresh) _index = Math.Min(_index + 1, sets.Length - 1);
                return Task.FromResult(sets[_index]);
            }
        }
    }

    private sealed class SignedOutSource : IAwsCredentialSource
    {
        public Task<AwsCredentials> GetAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
            throw new AwsSignInRequiredException("dsh-bedrock", "The SSO session has expired.");
    }

    private static string Disposition(Exception error) => RequestRetry.Disposition(error).GetType().Name;

    // MARK: Streaming

    [Fact]
    public async Task StreamsTextAndReportsUsage()
    {
        var handler = new FakeHttpHandler(_ => Reply(MessageStart(), TextDelta(0, "Hel"), TextDelta(0, "lo"), BlockStop(0), Stop("end_turn"),
                                                      Metadata(input: 10, output: 5, cacheRead: 100, cacheWrite: 3)));
        var result = await CollectAsync(Client(handler));

        Assert.Equal("Hello", result.Text);
        Assert.Empty(result.Done.Calls);
        Assert.Equal("stop", result.Done.Finish);
        Assert.Equal(new LlmUsage(113, 5), result.Done.Usage);
        Assert.Null(result.Done.ProviderState);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal($"{Runtime}/model/us.anthropic.claude-sonnet-4-5-20250929-v1%3A0/converse-stream", sent.Url.AbsoluteUri);
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20260929/us-east-1/bedrock/aws4_request, " +
                          "SignedHeaders=content-type;host;x-amz-date;x-amz-security-token, Signature=", sent.Headers["Authorization"]);
        Assert.Equal("20260929T123456Z", sent.Headers["X-Amz-Date"]);
        Assert.Equal(SigV4Tests.Token, sent.Headers["X-Amz-Security-Token"]);
        var body = JsonNode.Parse(sent.Body)!.AsObject();
        Assert.Equal("hi", body["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task ToolInputSplitAcrossDeltasIsAssembled()
    {
        var handler = new FakeHttpHandler(_ => Reply(MessageStart(), TextDelta(0, "On it."), ToolStart(1, "tooluse_abc", "echo"),
                                                      ToolInput(1, """{"te"""), ToolInput(1, """xt":"hi"}"""), BlockStop(1),
                                                      ToolStart(2, "tooluse_def", "list"), BlockStop(2),
                                                      Stop("tool_use"), Metadata(1, 1)));
        var result = await CollectAsync(Client(handler));

        Assert.Equal("On it.", result.Text);
        Assert.Equal("tool_calls", result.Done.Finish);
        Assert.Equal([new ToolCall("tooluse_abc", "echo", """{"text":"hi"}"""), new ToolCall("tooluse_def", "list", "{}")], result.Done.Calls);
        Assert.Null(result.Done.ProviderState); // nothing signed to send back
    }

    [Fact]
    public async Task ReasoningStreamsAndItsSignedBlocksBecomeProviderState()
    {
        var handler = new FakeHttpHandler(_ => Reply(MessageStart(), Reasoning(0, "Let me "), Reasoning(0, "think."), Reasoning(0, signature: "sig=="),
                                                      BlockStop(0), Reasoning(1, redacted: "AQID"), TextDelta(2, "Calling."),
                                                      ToolStart(3, "tooluse_1", "echo"), ToolInput(3, """{"text":"x"}"""),
                                                      Stop("tool_use"), Metadata(1, 1)));
        var result = await CollectAsync(Client(handler, profile: Profile(Opus47)));

        Assert.Equal("Let me think.", result.Reasoning);
        Assert.Equal("Calling.", result.Text);
        Assert.Equal(
            """[{"reasoningContent":{"reasoningText":{"text":"Let me think.","signature":"sig=="}}},""" +
            """{"reasoningContent":{"redactedContent":"AQID"}},{"text":"Calling."},""" +
            """{"toolUse":{"toolUseId":"tooluse_1","name":"echo","input":{"text":"x"}}}]""",
            result.Done.ProviderState);
    }

    [Theory]
    [InlineData("max_tokens", "length")]
    [InlineData("stop_sequence", "stop")]
    [InlineData("guardrail_intervened", "guardrail_intervened")]
    public async Task StopReasonsMapToFinishWords(string reason, string finish)
    {
        var handler = new FakeHttpHandler(_ => Reply(TextDelta(0, "x"), Stop(reason)));
        Assert.Equal(finish, (await CollectAsync(Client(handler))).Done.Finish);
    }

    [Fact]
    public async Task ContextWindowExceededIsAnOverflow()
    {
        var handler = new FakeHttpHandler(_ => Reply(TextDelta(0, "partial"), Stop("model_context_window_exceeded")));
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(handler)));
        Assert.Equal(LlmErrorKind.Overflow, error.Kind);
    }

    [Fact]
    public async Task StreamWithoutMessageStopWasCutOff()
    {
        var handler = new FakeHttpHandler(_ => Reply(MessageStart(), TextDelta(0, "Hel")));
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(handler)));
        Assert.Equal(LlmErrorKind.Sse, error.Kind);
        Assert.Equal("UntilAvailable", Disposition(error));
    }

    [Fact]
    public async Task CorruptFrameIsARetryableStreamError()
    {
        var frame = TextDelta(0, "Hello");
        frame[^6] ^= 0x20;
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(new FakeHttpHandler(_ => Reply(frame)))));
        Assert.Equal(LlmErrorKind.Sse, error.Kind);
        Assert.Contains("checksum", error.Message);
    }

    [Theory]
    [InlineData("throttlingException", "Too many tokens, please wait.", 429, "UntilAvailable")]
    [InlineData("serviceUnavailableException", "Bedrock is unable to process your request.", 503, "UntilAvailable")]
    [InlineData("internalServerException", "Internal error", 500, "Limited")]
    [InlineData("modelStreamErrorException", "The model stream failed.", 500, "Limited")]
    [InlineData("validationException", "The format of the value at messages.3 is invalid.", 400, "Fail")]
    public async Task ExceptionFramesMapLikeHttpErrors(string type, string message, int status, string disposition)
    {
        var handler = new FakeHttpHandler(_ => Reply(MessageStart(), TextDelta(0, "par"), Exception(type, message)));
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(handler)));
        Assert.Equal(LlmErrorKind.Http, error.Kind);
        Assert.Equal(status, error.StatusCode);
        Assert.Equal(disposition, Disposition(error));
        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task OverflowInAnExceptionFrameCarriesTheLimit()
    {
        var handler = new FakeHttpHandler(_ => Reply(Exception("validationException", "prompt is too long: 250000 tokens > 200000 maximum")));
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(handler)));
        Assert.Equal(LlmErrorKind.Overflow, error.Kind);
        Assert.Equal(200_000, error.Limit);
    }

    [Fact]
    public async Task NonEventStreamReplyIsNotRetried()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"hello":"proxy"}"""));
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(handler)));
        Assert.Equal(LlmErrorKind.Http, error.Kind);
        Assert.Contains("unrecognised reply", error.Message);
        Assert.Equal("Fail", Disposition(error));
    }

    [Fact]
    public async Task ConnectionFailureIsAConnectionError()
    {
        var handler = new FakeHttpHandler(_ => throw new HttpRequestException(HttpRequestError.ConnectionError, "refused"));
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(handler)));
        Assert.Equal(LlmErrorKind.Connection, error.Kind);
        Assert.Equal("UntilAvailable", Disposition(error));
    }

    [Fact]
    public async Task CancellationStopsTheStream()
    {
        var bytes = MessageStart().Concat(TextDelta(0, "first")).ToArray();
        var handler = new FakeHttpHandler(_ =>
        {
            var content = new StreamContent(new HangingStream(bytes));
            content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.amazon.eventstream");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        });
        using var cts = new CancellationTokenSource();
        var seen = new List<LlmStreamEvent>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var ev in Client(handler).StreamAsync(Request(), cts.Token))
            {
                seen.Add(ev);
                cts.Cancel();
            }
        });
        Assert.Equal([new LlmStreamEvent.Text("first")], seen);
    }

    // MARK: HTTP errors

    [Theory]
    [InlineData(400, "ValidationException:http://internal.amazon.com/coral/com.amazon.bedrock/", "Input is too long for requested model.", LlmErrorKind.Overflow, 400, "Fail")]
    [InlineData(400, "ValidationException", "input length and `max_tokens` exceed context limit: 180000 + 32000 > 200000", LlmErrorKind.Overflow, 400, "Fail")]
    [InlineData(400, "ValidationException", "The maximum tokens you requested exceeds the model limit of 8192.", LlmErrorKind.Http, 400, "Fail")]
    [InlineData(403, "AccessDeniedException", "You don't have access to the model with the specified model ID.", LlmErrorKind.Http, 403, "Fail")]
    [InlineData(404, "ResourceNotFoundException", "Model not found.", LlmErrorKind.Http, 404, "Fail")]
    [InlineData(429, "ThrottlingException", "Too many requests, please wait before trying again.", LlmErrorKind.Http, 429, "UntilAvailable")]
    [InlineData(429, "ServiceQuotaExceededException", "Your request exceeds the service quota for your account.", LlmErrorKind.Http, 429, "UntilAvailable")]
    [InlineData(429, "ModelNotReadyException", "Model is not ready for inference.", LlmErrorKind.Http, 429, "UntilAvailable")]
    [InlineData(503, "ServiceUnavailableException", "Service unavailable.", LlmErrorKind.Http, 503, "UntilAvailable")]
    [InlineData(500, "InternalServerException", "Internal server error.", LlmErrorKind.Http, 500, "Limited")]
    [InlineData(424, "ModelErrorException", "The model failed.", LlmErrorKind.Http, 500, "Limited")]
    [InlineData(408, "ModelTimeoutException", "The model timed out.", LlmErrorKind.Http, 408, "UntilAvailable")]
    public async Task HttpErrorsMapToTheRightRetryClass(int status, string type, string message, LlmErrorKind kind, int code, string disposition)
    {
        var handler = new FakeHttpHandler(_ => Error((HttpStatusCode)status, type, message));
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(handler)));
        Assert.Equal(kind, error.Kind);
        Assert.Equal(code, error.StatusCode);
        Assert.Equal(disposition, Disposition(error));
        Assert.Contains(message, error.Message);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AccessDeniedNamesTheModelAndTheSubscriptionDelay()
    {
        var handler = new FakeHttpHandler(_ => Error(HttpStatusCode.Forbidden, "AccessDeniedException",
            "Model access is denied due to IAM user or service role is not authorized to perform the required AWS Marketplace actions (aws-marketplace:ViewSubscriptions, aws-marketplace:Subscribe)."));
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(handler)));
        Assert.Contains(Sonnet45, error.Message);
        Assert.Contains("us-east-1", error.Message);
        Assert.Contains("Settings › Models", error.Message);
        Assert.Contains("15 minutes", error.Message);
    }

    [Fact]
    public async Task MessageIsReadFromACapitalisedFieldToo()
    {
        var handler = new FakeHttpHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"Message":"Malformed input request"}""", Encoding.UTF8, "application/json"),
            };
            response.Headers.TryAddWithoutValidation("x-amzn-ErrorType", "ValidationException");
            return response;
        });
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(handler)));
        Assert.Contains("Malformed input request", error.Message);
        Assert.Equal("ValidationException: Malformed input request", error.Body);
    }

    // MARK: Credentials

    [Fact]
    public async Task ExpiredCredentialsAreRefreshedOnceAndTheRequestResent()
    {
        var source = new RotatingSource(new AwsCredentials("OLDKEY", "old", "old-token"), SigV4Tests.WithToken);
        var n = 0;
        var handler = new FakeHttpHandler(_ => Interlocked.Increment(ref n) == 1
            ? Error(HttpStatusCode.Forbidden, "ExpiredTokenException", "The security token included in the request is expired")
            : Reply(TextDelta(0, "fresh"), Stop("end_turn")));
        var result = await CollectAsync(Client(handler, source));

        Assert.Equal("fresh", result.Text);
        Assert.Equal([false, true], source.Calls);
        Assert.Contains("Credential=OLDKEY/", handler.Requests[0].Headers["Authorization"]);
        Assert.Contains("Credential=AKIDEXAMPLE/", handler.Requests[1].Headers["Authorization"]);
    }

    [Fact]
    public async Task CredentialsStillRejectedAfterARefreshSurfacePermanently()
    {
        var source = new RotatingSource(SigV4Tests.WithToken);
        var handler = new FakeHttpHandler(_ => Error(HttpStatusCode.Forbidden, "UnrecognizedClientException",
                                                     "The security token included in the request is invalid."));
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(handler, source)));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("Fail", Disposition(error));
        Assert.Contains("aws login --profile dsh-bedrock", error.Message);
    }

    [Fact]
    public async Task ClockSkewIsCorrectedFromAwsTime()
    {
        var n = 0;
        var handler = new FakeHttpHandler(_ =>
        {
            if (Interlocked.Increment(ref n) > 1) return Reply(TextDelta(0, "ok"), Stop("end_turn"));
            var response = Error(HttpStatusCode.Forbidden, "InvalidSignatureException",
                "Signature expired: 20260929T123456Z is now earlier than 20260929T124456Z (20260929T124956Z - 5 min.)");
            response.Headers.Date = SigV4Tests.Now.AddMinutes(15);
            return response;
        });
        var result = await CollectAsync(Client(handler));
        Assert.Equal("ok", result.Text);
        Assert.Equal("20260929T124956Z", handler.Requests[1].Headers["X-Amz-Date"]);
    }

    [Fact]
    public async Task SignInRequiredIsAPermanentErrorThatSaysHowToSignIn()
    {
        var handler = new FakeHttpHandler(_ => Reply(Stop("end_turn")));
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(handler, new SignedOutSource())));

        Assert.Equal("Fail", Disposition(error));
        Assert.Contains("expired", error.Message);
        Assert.Contains("Settings › Models", error.Message);
        Assert.Contains("aws login --profile dsh-bedrock", error.Message);
        Assert.IsType<AwsSignInRequiredException>(error.InnerException);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NoCredentialSourceAsksForASignIn()
    {
        var handler = new FakeHttpHandler(_ => Reply(Stop("end_turn")));
        var client = new BedrockClient(Profile(), null, handler);
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(client));
        Assert.Equal("Fail", Disposition(error));
        Assert.Contains("sign-in", error.Message);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ApiKeyIsSentAsABearerToken()
    {
        var handler = new FakeHttpHandler(_ => Reply(TextDelta(0, "ok"), Stop("end_turn")));
        var profile = Profile();
        profile.AwsProfile = null;
        profile.ApiKey = "bedrock-api-key-abc";
        await CollectAsync(new BedrockClient(profile, null, handler));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("Bearer bedrock-api-key-abc", sent.Headers["Authorization"]);
        Assert.False(sent.Headers.ContainsKey("X-Amz-Date"));
    }

    [Fact]
    public async Task RejectedApiKeyIsPermanent()
    {
        var handler = new FakeHttpHandler(_ => Error(HttpStatusCode.Forbidden, "AccessDeniedException", "Authentication failed: Please make sure your API Key is valid."));
        var profile = Profile();
        profile.AwsProfile = null;
        profile.ApiKey = "bedrock-api-key-old";
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(new BedrockClient(profile, null, handler)));
        Assert.Contains("API key", error.Message);
        Assert.Equal("Fail", Disposition(error));
        Assert.Single(handler.Requests);
    }

    // MARK: Endpoints

    [Fact]
    public async Task EndpointOverrideIsUsedAndSignedForTheProfileRegion()
    {
        var handler = new FakeHttpHandler(_ => Reply(TextDelta(0, "ok"), Stop("end_turn")));
        var profile = Profile();
        profile.BaseUrl = "https://vpce-0abc.bedrock-runtime.us-west-2.vpce.amazonaws.com/";
        profile.AwsRegion = "us-west-2";
        await CollectAsync(Client(handler, profile: profile));

        var sent = Assert.Single(handler.Requests);
        Assert.Equal("vpce-0abc.bedrock-runtime.us-west-2.vpce.amazonaws.com", sent.Url.Host);
        Assert.StartsWith("/model/us.anthropic.claude-sonnet-4-5-20250929-v1%3A0/converse-stream", sent.Url.AbsolutePath);
        Assert.Contains("/us-west-2/bedrock/aws4_request", sent.Headers["Authorization"]);
    }

    [Fact]
    public void RegionAndControlPlaneFollowTheRuntimeUrl()
    {
        var profile = Profile();
        profile.AwsRegion = null;
        profile.BaseUrl = "https://bedrock-runtime.eu-west-1.amazonaws.com";
        var client = new BedrockClient(profile, null);
        Assert.Equal("eu-west-1", client.Region);
        Assert.Equal("https://bedrock.eu-west-1.amazonaws.com", client.ControlBase);

        profile.BaseUrl = "";
        profile.AwsRegion = "ap-northeast-1";
        Assert.Equal("https://bedrock-runtime.ap-northeast-1.amazonaws.com", client.RuntimeBase);
        Assert.Equal("https://bedrock.ap-northeast-1.amazonaws.com", client.ControlBase);
        Assert.Equal("https://bedrock-runtime.ap-northeast-1.amazonaws.com/model/arn%3Aaws%3Abedrock%3Aap-northeast-1%3A1%3Ainference-profile%2Fapac.x/converse-stream",
            client.ConverseStreamUrl("arn:aws:bedrock:ap-northeast-1:1:inference-profile/apac.x").AbsoluteUri);

        // A public endpoint left over from before the Region changed follows the Region.
        profile.BaseUrl = "https://bedrock-runtime.us-east-1.amazonaws.com";
        Assert.Equal("https://bedrock-runtime.ap-northeast-1.amazonaws.com", client.RuntimeBase);
    }

    [Fact]
    public async Task RequestTooLargeIsTreatedAsAnOverflow()
    {
        var handler = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.RequestEntityTooLarge));
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(handler)));
        Assert.Equal(LlmErrorKind.Overflow, error.Kind);
    }

    [Fact]
    public async Task MissingRegionIsAPermanentError()
    {
        var profile = Profile();
        profile.AwsRegion = null;
        profile.BaseUrl = "";
        var error = await Assert.ThrowsAsync<LlmException>(() => CollectAsync(Client(new FakeHttpHandler(_ => Reply()), profile: profile)));
        Assert.Contains("Region", error.Message);
        Assert.Equal("Fail", Disposition(error));
    }

    // MARK: Reasoning replay

    private static LlmRequest ToolTurnRequest(string state) => new("You are DSH.",
    [
        LlmMessage.User("go"),
        LlmMessage.Assistant("", [new ToolCall("tooluse_1", "echo", "{}")]) with { ProviderState = state },
        LlmMessage.ToolOutput("tooluse_1", "echo", "ok"),
    ], [new ToolSpec("echo", "echo", "{}")], Opus47, Thinking: ThinkingLevel.High);

    private const string SignedState =
        """[{"reasoningContent":{"reasoningText":{"text":"t","signature":"s"}}},{"toolUse":{"toolUseId":"tooluse_1","name":"echo","input":{}}}]""";

    [Fact]
    public async Task RefusedReasoningIsDroppedAndTheRequestSentAgain()
    {
        var n = 0;
        var handler = new FakeHttpHandler(_ => Interlocked.Increment(ref n) == 1
            ? Error(HttpStatusCode.BadRequest, "ValidationException",
                    "messages.1.content.0: Invalid `signature` in `thinking` block. The block is bound to a different conversation.")
            : Reply(TextDelta(0, "done"), Stop("end_turn")));
        var result = await CollectAsync(Client(handler, profile: Profile(Opus47)), ToolTurnRequest(SignedState));

        Assert.Equal("done", result.Text);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("reasoningContent", handler.Requests[0].Body);
        Assert.DoesNotContain("reasoningContent", handler.Requests[1].Body);
    }

    [Fact]
    public async Task EngineCarriesSignedReasoningIntoTheNextBedrockRequest()
    {
        var n = 0;
        var handler = new FakeHttpHandler(_ => Interlocked.Increment(ref n) == 1
            ? Reply(Reasoning(0, "plan"), Reasoning(0, signature: "sig-1"), ToolStart(1, "tooluse_9", EchoTool.ToolName),
                    ToolInput(1, """{"text":"ping"}"""), Stop("tool_use"))
            : Reply(TextDelta(0, "all done"), Stop("end_turn")));
        using var root = new TempDirectory("dsh-bedrock");
        var profile = Profile(Sonnet45);
        var engine = new Engine(Client(handler, profile: profile), new ToolRegistry([new EchoTool()]), "system",
            new EngineConfig(Sonnet45) { Thinking = ThinkingLevel.High, Retry = RetryPolicy.Off },
            root.Path, new PermissionPolicy(PermissionPreset.FullAccess, root.Path), Gates.Allow);
        var result = await engine.RunAsync([], "go");

        Assert.Equal("all done", result.FinalText);
        var second = JsonNode.Parse(handler.Requests[1].Body)!.AsObject();
        var assistant = second["messages"]![1]!["content"]!.AsArray();
        Assert.Equal("sig-1", assistant[0]!["reasoningContent"]!["reasoningText"]!["signature"]!.GetValue<string>());
        Assert.Equal("tooluse_9", assistant[1]!["toolUse"]!["toolUseId"]!.GetValue<string>());
        Assert.Equal(16_000, second["additionalModelRequestFields"]!["thinking"]!["budget_tokens"]!.GetValue<int>());
    }

    // MARK: Models

    [Fact]
    public async Task ListModelsReturnsOnDemandModelsThenEveryInferenceProfilePage()
    {
        var handler = new FakeHttpHandler(request =>
        {
            var url = request.RequestUri!;
            if (url.AbsolutePath == "/foundation-models")
            {
                return FakeHttpHandler.Json("""
                    {"modelSummaries":[
                      {"modelId":"anthropic.claude-sonnet-4-5-20250929-v1:0","inferenceTypesSupported":["INFERENCE_PROFILE"],"responseStreamingSupported":true},
                      {"modelId":"amazon.nova-pro-v1:0","inferenceTypesSupported":["ON_DEMAND"],"responseStreamingSupported":true},
                      {"modelId":"amazon.titan-tg1-large","inferenceTypesSupported":["ON_DEMAND"],"responseStreamingSupported":false},
                      {"modelId":"meta.llama3-3-70b-instruct-v1:0","inferenceTypesSupported":["ON_DEMAND","PROVISIONED"]}]}
                    """);
            }
            if (url.AbsolutePath == "/inference-profiles")
            {
                return url.Query.Contains("nextToken=page%2F2%2B%3D%3D", StringComparison.Ordinal)
                    ? FakeHttpHandler.Json("""{"inferenceProfileSummaries":[{"inferenceProfileId":"us.amazon.nova-pro-v1:0","status":"ACTIVE"}]}""")
                    : FakeHttpHandler.Json("""{"inferenceProfileSummaries":[{"inferenceProfileId":"us.anthropic.claude-sonnet-4-5-20250929-v1:0","status":"ACTIVE"}],"nextToken":"page/2+=="}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var models = await Client(handler).ListModelsAsync();

        Assert.Equal(["amazon.nova-pro-v1:0", "meta.llama3-3-70b-instruct-v1:0", "us.anthropic.claude-sonnet-4-5-20250929-v1:0", "us.amazon.nova-pro-v1:0"],
            models);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r =>
        {
            Assert.Equal("bedrock.us-east-1.amazonaws.com", r.Url.Host);
            Assert.Equal(HttpMethod.Get, r.Method);
            Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20260929/us-east-1/bedrock/aws4_request", r.Headers["Authorization"]);
        });
        Assert.Equal("?byOutputModality=TEXT", handler.Requests[0].Url.Query);
    }

    [Fact]
    public async Task ListModelsErrorsAreMapped()
    {
        var handler = new FakeHttpHandler(_ => Error(HttpStatusCode.Forbidden, "AccessDeniedException",
            "User: arn:aws:iam::1:user/x is not authorized to perform: bedrock:ListFoundationModels"));
        var error = await Assert.ThrowsAsync<LlmException>(() => Client(handler).ListModelsAsync());
        Assert.Equal(403, error.StatusCode);
    }

    [Theory]
    [InlineData(Sonnet45, 200_000)]
    [InlineData("arn:aws:bedrock:us-west-2:123456789012:inference-profile/us.anthropic.claude-opus-4-1-20250805-v1:0", 200_000)]
    [InlineData("global.anthropic.claude-opus-4-6-v1", 1_000_000)]
    [InlineData("anthropic.claude-haiku-4-5-20251001-v1:0", 200_000)]
    [InlineData("amazon.nova-pro-v1:0", 300_000)]
    [InlineData("us.amazon.nova-lite-v1:0", 300_000)]
    [InlineData("amazon.nova-micro-v1:0", 128_000)]
    [InlineData("meta.llama3-3-70b-instruct-v1:0", 128_000)]
    [InlineData("mistral.mistral-large-2407-v1:0", 128_000)]
    [InlineData("us.deepseek.r1-v1:0", 128_000)]
    [InlineData("openai.gpt-oss-120b-1:0", 128_000)]
    [InlineData("qwen.qwen3-32b-v1:0", 128_000)]
    public async Task ModelInfoComesFromTheTable(string model, int window)
    {
        var info = await Client(new FakeHttpHandler(_ => throw new InvalidOperationException("no network"))).ModelInfoAsync(model);
        Assert.Equal(model, info.Id);
        Assert.Equal(window, info.ContextWindow);
        Assert.Equal(window, FallbackContextWindow.Limit(model));
    }

    [Fact]
    public async Task UnknownModelsHaveNoWindow()
    {
        var info = await Client(new FakeHttpHandler(_ => Reply())).ModelInfoAsync("arn:aws:bedrock:us-east-1:1:application-inference-profile/abc123");
        Assert.Null(info.ContextWindow);
        Assert.Equal(64_000, (await Client(new FakeHttpHandler(_ => Reply())).ModelInfoAsync(Sonnet45)).MaxTokens);
    }

    [Fact]
    public void FallbackTablesStillServeOtherProviders()
    {
        Assert.Equal(128_000, FallbackContextWindow.Limit("gpt-4o"));
        Assert.Equal(200_000, FallbackContextWindow.Limit("anthropic/claude-sonnet-4.5"));
        Assert.Null(BedrockModels.ContextWindow("claude-sonnet-4-5"));
    }

    /// <summary>Returns its bytes, then blocks until cancelled.</summary>
    private sealed class HangingStream(byte[] data) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < data.Length)
            {
                var n = Math.Min(buffer.Length, data.Length - _position);
                data.AsMemory(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }
    }
}

/// <summary>ProviderClients picks the client by profile kind and wires Bedrock to the app's credential
/// source. It mutates a process-wide hook, so it runs apart from the parallel tests.</summary>
[Collection(SerialStaticState.Name)]
public sealed class ProviderClientsTests
{
    [Fact]
    public void BedrockProfilesGetABedrockClientWithTheAppsCredentials()
    {
        var previous = ProviderClients.AwsCredentials;
        var asked = new List<string?>();
        try
        {
            ProviderClients.AwsCredentials = profile =>
            {
                asked.Add(profile.AwsProfile);
                return new StaticAwsCredentialSource(SigV4Tests.NoToken);
            };
            var bedrock = new ProviderProfile(ProviderKind.Bedrock, "B", "", "amazon.nova-pro-v1:0") { AwsProfile = "dsh-bedrock", AwsRegion = "us-east-1" };
            Assert.IsType<BedrockClient>(ProviderClients.Create(bedrock));
            Assert.Equal(["dsh-bedrock"], asked);

            var keyed = new ProviderProfile(ProviderKind.Bedrock, "B", "", "amazon.nova-pro-v1:0") { ApiKey = "bedrock-api-key-x", AwsRegion = "us-east-1" };
            var client = Assert.IsType<BedrockClient>(ProviderClients.Create(keyed));
            Assert.True(client.UsesApiKey);
            Assert.Single(asked); // an API-key profile needs no AWS credentials

            var openAi = ProviderClients.Create(new ProviderProfile(ProviderKind.OpenAI, "O", "https://api.openai.com/v1", "gpt-4o"));
            Assert.IsType<OpenAiClient>(openAi);
            Assert.Equal("gpt-4o", openAi.Profile.Model);
        }
        finally
        {
            ProviderClients.AwsCredentials = previous;
        }
    }

    [Fact]
    public async Task WithoutACredentialHookBedrockAsksForASignIn()
    {
        var previous = ProviderClients.AwsCredentials;
        try
        {
            ProviderClients.AwsCredentials = null;
            var profile = new ProviderProfile(ProviderKind.Bedrock, "B", "", "amazon.nova-pro-v1:0") { AwsProfile = "p", AwsRegion = "us-east-1" };
            var client = ProviderClients.Create(profile, new FakeHttpHandler(_ => EventStreamFrames.Reply()));
            var error = await Assert.ThrowsAsync<LlmException>(async () =>
            {
                await foreach (var _ in client.StreamAsync(new LlmRequest("s", [LlmMessage.User("hi")], [], ""))) { }
            });
            Assert.Equal("Fail", RequestRetry.Disposition(error).GetType().Name);
        }
        finally
        {
            ProviderClients.AwsCredentials = previous;
        }
    }
}
