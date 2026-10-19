using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vision.Core;

// MARK: - The Guardian sign-in this PC keeps
//
// Who is signed in, where, and which org lives in guardian.json in the data folder (no secrets in it). The session
// token lives in Credential Manager as Vision/guardian/token — in parts when it is longer than one entry holds — and
// is also every Guardian route's key. The password is never kept anywhere.

public sealed record GuardianAccount
{
    public string Address { get; init; } = GuardianClient.DefaultAddress;
    public string Email { get; init; } = "";
    public string? Name { get; init; }
    public string? UserId { get; init; }
    /// <summary>The org calls go to (x-guardian-org); null = the account's default.</summary>
    public string? OrgId { get; init; }
    public string? OrgName { get; init; }
    /// <summary>"password" or "browser".</summary>
    public string Method { get; init; } = "password";
    public DateTimeOffset SignedInAt { get; init; } = DateTimeOffset.Now;

    [JsonIgnore] public string Label => string.IsNullOrWhiteSpace(Name) ? Email : $"{Name} ({Email})";
}

public sealed class GuardianAccountStore
{
    /// <summary>Credential Manager's name for the session token.</summary>
    public const string TokenTarget = SecretStore.Prefix + "guardian/token";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public GuardianAccountStore(string? folder = null) => Path = System.IO.Path.Combine(folder ?? AppPaths.Root, "guardian.json");

    public string Path { get; }

    /// <summary>The account and its token, or null when signed out (or the token is gone from Credential Manager).</summary>
    public (GuardianAccount Account, string Token)? Load()
    {
        GuardianAccount? account;
        try
        {
            if (!File.Exists(Path)) return null;
            account = JsonSerializer.Deserialize<GuardianAccount>(File.ReadAllText(Path), Json);
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
        if (account is null || GuardianClient.NormalizeAddress(account.Address) is not { } address) return null;
        string? token;
        try
        {
            token = SecretStore.Read(TokenTarget);
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            token = null;
        }
        return string.IsNullOrWhiteSpace(token) ? null : (account with { Address = address }, token);
    }

    /// <summary>The last account, signed in or not (to fill the sign-in form's address and email).</summary>
    public GuardianAccount? LastAccount()
    {
        try
        {
            return File.Exists(Path) ? JsonSerializer.Deserialize<GuardianAccount>(File.ReadAllText(Path), Json) : null;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Keep the account, and its token in Credential Manager. Throws when the token can't be stored.</summary>
    public void Save(GuardianAccount account, string token)
    {
        SecretStore.Write(TokenTarget, token, account.Email);
        Update(account);
    }

    /// <summary>Change what is known about the account (its org, its name) without touching the token.</summary>
    public void Update(GuardianAccount account)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(account, Json));
        File.Move(temp, Path, overwrite: true);
    }

    /// <summary>Sign out on this PC: the token goes; the address and email stay for the next sign-in.</summary>
    public void SignOut()
    {
        try { SecretStore.Delete(TokenTarget); } catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.ExternalException) { }
    }
}
