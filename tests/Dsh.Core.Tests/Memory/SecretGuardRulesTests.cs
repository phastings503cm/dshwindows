namespace Dsh.Core.Tests;

/// <summary>What the secret scanner makes of the shapes a review of it turned up: names dressed in unusual ways, values in other layouts,
/// keys of services it had not heard of — and, as much, the notes, pointers and code that only look like leaks.</summary>
public sealed class SecretGuardRulesTests
{
    [Theory]
    [InlineData("DB_PASS=Xk29mQ788abZ")]
    [InlineData("SMTP_PASS=Xk29mQ788abZ")]
    [InlineData("REDIS_PASS=Xk29mQ788abZ1234")]
    [InlineData("ADMIN_PW=Xk29mQ788abZ")]
    [InlineData("db_pass: Xk29mQ788abZ")]
    [InlineData("dbPass=Xk29mQ788abZ")]
    [InlineData("ansible_become_pass: Xk29mQ788abZ")]
    [InlineData("The password is now Xk29mQ788abZ")]
    [InlineData("The password was changed to Xk29mQ788abZ")]
    [InlineData("Set the password to Xk29mQ788abZ")]
    [InlineData("login as admin with password Xk29mQ788abZ")]
    [InlineData("staging pw Xk29mQ788abZ")]
    [InlineData("machine h login bob password Xk29mQ788abZ")]
    [InlineData("aws configure set aws_secret_access_key wJalrXUtnFEMI/K7MDENG/bPxRfiCYzZzZzZzZzZ")]
    [InlineData("create user app with password 'Xk29mQ788abZ';")]
    [InlineData("GRANT ALL ON db.* TO app IDENTIFIED BY 'Xk29mQ788abZ'")]
    [InlineData("curl -u admin:Xk29mQ788abZ https://example.com/api")]
    [InlineData("docker login -u bob -p Xk29mQ788abZ")]
    [InlineData("mysql -u root -pXk29mQ788abZ")]
    [InlineData("password:\nXk29mQ788abZ")]
    [InlineData("- name: DB_PASSWORD\n  value: Xk29mQ788abZ")]
    [InlineData("{\"name\":\"DB_PASSWORD\",\"value\":\"Xk29mQ788abZ\"}")]
    [InlineData("<password>Xk29mQ788abZ</password>")]
    [InlineData("<add key=\"ClearTextPassword\" value=\"Xk29mQ788abZ\"/>")]
    [InlineData("os.environ[\"DB_PASSWORD\"] = \"Xk29mQ788abZ\"")]
    [InlineData("Environment.SetEnvironmentVariable(\"DB_PASSWORD\",\"Xk29mQ788abZ\")")]
    [InlineData("conf.set(\"db.password\",\"Xk29mQ788abZ\")")]
    [InlineData("password: $ecureP4ssw0rd")]
    [InlineData("DB_PASSWORD=%Xk29mQ788abZ")]
    [InlineData("{\"password\": \"$ecureP4ss!\"}")]
    [InlineData("postgres://app:%40dmin2024@db.internal/app")]
    [InlineData("mysql://root:$ecureP4ss!@h/db")]
    [InlineData("redis://:#Secret123@cache")]
    [InlineData("var password = @\"Xk29mQ788abZ\";")]
    [InlineData("password = \"\"\"Xk29mQ788abZ\"\"\"")]
    [InlineData("AWS_BEARER_TOKEN_BEDROCK=bedrock-api-key-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd")]
    [InlineData("bedrock-api-key-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789abcd")]
    [InlineData("API_KEY_OPENROUTER=abc123def456ghi789")]
    [InlineData("DB_PASSWORD_READONLY=Xk29mQ788abZ")]
    [InlineData("TOKEN_TELEGRAM=1234567890:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw")]
    [InlineData("GITHUB_TOKEN_CI=abc123def456ghi789jkl")]
    [InlineData("SPARK_KEY=spk-live-4f8a1c9d2e7b6a305d")]
    [InlineData("STRIPE_KEY=sk123abc456def789ghi012jkl")]
    [InlineData("SESSION_KEY=4f8a1c9d2e7b6a305d4f8a1c9d2e7b6a")]
    [InlineData("AES_KEY=4f8a1c9d2e7b6a305d4f8a1c9d2e7b6a")]
    [InlineData("HMAC_KEY=Xk29mQ788abZ4f8a1c9d2e7b")]
    [InlineData("Authorization: Token 4f9a1b2c3d4e5f604f9a1b2c3d4e5f60aabbccdd")]
    [InlineData("Authorization: Api-Key abc123def456ghi789")]
    [InlineData("Authorization: Bearer abc123def456")]
    [InlineData("https://hooks.slack.com/services/T0ABCDEF1/" + "B0ABCDEF1/abcdefghijklmnopqrstuvwx")]
    [InlineData("https://discord.com/api/webhooks/123456789012345678/abcdefghijklmnopqrstuvwxyzABCDEFGHIJKL")]
    [InlineData("xai-abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGH")]
    [InlineData("rk_" + "live_abcdefghijklmnopqrstuvwx")]
    [InlineData("dop_v1_abcdef0123456789abcdef0123456789abcdef0123456789")]
    [InlineData("ya29.a0AfH6SMBabcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("SG.abcdefghijklmnopqrstuv." + "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFG")]
    [InlineData("-----BEGIN PGP PRIVATE KEY BLOCK-----\nlQdGBF0abcdefgh")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA")]
    [InlineData("client-key-data: LS0tLS1CRUdJTiBSU0EgUFJJVkFURSBLRVktLS0tLQpNSUlFcGdJQkFBS0NBUUVB")]
    [InlineData("\"auth\": \"dXNlcjpwYXNzd29yZDEyMzQ1Njc4OTA=\"")]
    [InlineData("数据库密码：Xk29mQ788abZ")]
    [InlineData("password：Xk29mQ788abZ")]
    [InlineData("Passwort: Xk29mQ788abZ")]
    [InlineData("contraseña: Xk29mQ788abZ")]
    public void ACredentialIsRecognisedInAnyOfTheseShapes(string text) => Assert.True(SecretGuard.LooksLikeSecret(text), text);

    [Theory]
    [InlineData("Secret name: prod-db-creds-v3")]
    [InlineData("JWT secret lives in AWS Secrets Manager: prod/app/jwt-secret-v2")]
    [InlineData("Secret ARN: arn:aws:secretsmanager:us-east-1:123456789012:secret:prod/db-AbCdEf")]
    [InlineData("Encryption key ID: alias/prod-app-2")]
    [InlineData("Private key: C:\\Users\\patrick\\.ssh\\id_ed25519")]
    [InlineData("The signing key is Ed25519")]
    [InlineData("Access token TTL: 24hours")]
    [InlineData("Password last changed: 2024-01-15")]
    [InlineData("Token TTL: 3600ms")]
    [InlineData("botToken: \"123456:ABC...\"")]
    [InlineData("apiKey: \"env:AWS_BEARER_TOKEN_BEDROCK\"")]
    [InlineData("\"clientRequestToken\": \"550e8400-e29b-41d4-a716-446655440000\"")]
    [InlineData("\"NextToken\": \"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA1234\"")]
    [InlineData("postgresql://scott:tiger@db/x")]
    [InlineData("The header is -----BEGIN RSA PRIVATE KEY----- and then the body")]
    [InlineData("-----BEGIN PRIVATE KEY-----\n<paste your key here>\n-----END PRIVATE KEY-----")]
    [InlineData("var apiKey = Environment.GetEnvironmentVariable(\"OPENAI_API_KEY\");")]
    [InlineData("string token = GetAuthenticationToken();")]
    [InlineData("var secret = configuration.GetValue<string>(\"Jwt:Secret\");")]
    [InlineData("private_key = ed25519.Ed25519PrivateKey.generate()")]
    [InlineData("token = uuid.uuid4().hex")]
    [InlineData("password = hashlib.sha256(raw).hexdigest()")]
    [InlineData("if (mode === \"token\" || !hasInlineToken)")]
    [InlineData("echo $PASSWORD | sha256sum")]
    [InlineData("you can pass the kwargs is_xml=True.")]
    [InlineData("the token handler uses is_xml=True")]
    [InlineData("- name: JWT_SECRET_NAME\n  value: prod-jwt-secret-v2")]
    [InlineData("TOKEN_EXPIRY_SECONDS=3600")]
    [InlineData("TOKEN_URL=https://example.com/oauth/token")]
    [InlineData("PASSWORD_MIN_LENGTH=12")]
    [InlineData("PUBLIC_KEY=MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAxyz1234")]
    [InlineData("CACHE_KEY=user:123:profile")]
    [InlineData("the password manager 1Password")]
    [InlineData("password argon2id")]
    [InlineData("Use the token bucket algorithm")]
    [InlineData("The API key is stored in Azure Key Vault")]
    [InlineData("The token limit is 128000 tokens")]
    [InlineData("db password: see the vault")]
    [InlineData("SET password = NULL")]
    [InlineData("password_hash: $2b$12$abcdefghijklmnopqrstuvABCDEFGHIJKLMNOPQRSTUVWXYZ01234")]
    public void PointersToSecretsAndCodeThatHandlesThemAreNotLeaks(string text) => Assert.False(SecretGuard.LooksLikeSecret(text), text);

    [Theory]
    [InlineData("Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089")]
    [InlineData("<assemblyIdentity name=\"System.Buffers\" publicKeyToken=\"b03f5f7f11d50a3a\" culture=\"neutral\" />")]
    [InlineData("<PublicKeyToken>b03f5f7f11d50a3a</PublicKeyToken>")]
    [InlineData("var result = await cli.RunAsync(args, cancellationToken: cancellationToken).ConfigureAwait(false);")]
    [InlineData("foreach (var name in new[] { \"Authorization\", \"X-Amz-Date\", \"X-Amz-Security-Token\", \"X-Amz-Content-SHA256\" })")]
    [InlineData("if (CodeIs(\"ExpiredToken\", \"ExpiredTokenException\", \"RequestExpired\")) return;")]
    [InlineData("Sign(request, new AwsCredentials(\"OLDKEY\", \"old\", \"old-token\"), \"us-east-1\")")]
    [InlineData("string token = accessToken;")]
    [InlineData("client_secret: clientSecret")]
    public void CommonDotNetAndAwsCodeIsNotAskedToPassAsCredentials(string text) => Assert.False(SecretGuard.LooksLikeSecret(text), text);

    [Fact]
    public void ANotesTagsAreNotReadAsTheEndOfItsText()
    {
        // The machine tag ("h:" and a hash of the file) used to be glued on after the text, so a note that ended on "the staging
        // password" read as "the staging password file h: 2d711642b726" — and the chunk was thrown away as a credential.
        var notes = new[]
        {
            new MemoryDraft { Title = "Rotation", Body = "Remember to rotate the staging password", Tags = ["deploy", "h:2d711642b726", "whole"], Source = "file:x" },
            new MemoryDraft { Title = "Tokens", Body = "- rotate the API token", Tags = ["file", "h:2d711642b726", "whole"], Source = "file:y" },
            new MemoryDraft { Title = "Vault", Body = "The production database password", Tags = ["ops", "h:9a8b7c6d5e4f"], Source = "file:z" },
        };
        Assert.All(MemoryStore.FlagSecrets(notes), flagged => Assert.False(flagged));

        // A tag someone typed is still read.
        var typed = new[] { new MemoryDraft { Title = "Deploy", Body = "Steps for the deploy", Tags = ["DB_PASSWORD=Xk29mQ788abZ"], Source = "user" } };
        Assert.True(MemoryStore.FlagSecrets(typed)[0]);
    }

    [Fact]
    public void ASecretIsFoundWhereverItFallsInALongText()
    {
        // A long text is read in overlapping windows (32 KB, 4 KB shared): a secret must be found whichever side of an edge it lies.
        const string Secret = "DB_PASSWORD=Xk29mQ788abZ";
        var filler = string.Concat(Enumerable.Repeat("The quick brown fox jumps over the lazy dog, and the plan goes on. ", 1_100)); // about 75 KB, nothing secret
        Assert.False(SecretGuard.LooksLikeSecret(filler));
        foreach (var edge in new[] { 28_672, 32_768, 57_344, 61_440 })
        {
            for (var offset = -100; offset <= 100; offset += 7)
            {
                var text = filler.Insert(edge + offset, "\n" + Secret + "\n");
                Assert.True(SecretGuard.LooksLikeSecret(text), $"missed at {edge + offset}");
            }
        }
        Assert.True(SecretGuard.LooksLikeSecret(Secret + "\n" + filler));
        Assert.True(SecretGuard.LooksLikeSecret(filler + "\n" + Secret));
    }

    [Theory]
    [InlineData("You can commit directly to main.", "You couldn't commit directly to main.")]
    [InlineData("We commit directly to main.", "We haven't committed directly to main.")]
    [InlineData("The build passes on Windows.", "The build hasn't passed on Windows.")]
    [InlineData("She would merge to main.", "She wouldn't merge to main.")]
    public void AClaimTurnedAroundByAContractionIsAContradiction(string a, string b) => Assert.True(MemoryText.Contradicts(a, b));
}
