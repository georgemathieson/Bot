using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace TestSupport;

/// <summary>
/// A fake Entra ID tenant: one RSA signing key and helpers that mint tokens shaped like the real ones.
/// </summary>
public sealed class TestTenant : IDisposable
{
    public const string TenantId = "11111111-1111-1111-1111-111111111111";
    public const string ApiClientId = "22222222-2222-2222-2222-222222222222";
    public const string BotClientId = "33333333-3333-3333-3333-333333333333";
    public const string UserOid = "44444444-4444-4444-4444-444444444444";
    public const string OtherUserOid = "55555555-5555-5555-5555-555555555555";
    public const string BotAppIdUri = "api://botid-" + BotClientId;
    public const string ApiAppIdUri = "api://" + ApiClientId;
    public const string TeamsDesktopClientId = "1fec8e78-bce4-4aaf-ab1b-5451cc387264";
    public const string V2Issuer = "https://login.microsoftonline.com/" + TenantId + "/v2.0";
    public const string V1Issuer = "https://sts.windows.net/" + TenantId + "/";

    private readonly RSA _rsa = RSA.Create(2048);

    /// <param name="keyId">Key ID ("kid") of the signing key. Entra key rotation shows up as a new kid.</param>
    public TestTenant(string keyId = "test-key")
    {
        SigningKey = new RsaSecurityKey(_rsa) { KeyId = keyId };
        var config = new OpenIdConnectConfiguration { Issuer = V2Issuer };
        config.SigningKeys.Add(SigningKey);
        Configuration = config;
    }

    public RsaSecurityKey SigningKey { get; }

    public OpenIdConnectConfiguration Configuration { get; }

    /// <summary>Stands in for the tenant's OpenID metadata (signing keys) without any network call.</summary>
    public IConfigurationManager<OpenIdConnectConfiguration> ConfigurationManager =>
        new StaticConfigurationManager<OpenIdConnectConfiguration>(Configuration);

    /// <summary>What Teams hands the bot after silent SSO: issued to the Teams client, for the bot's API.</summary>
    public string SsoToken(Action<Dictionary<string, object>>? customize = null) =>
        Mint(new()
        {
            ["aud"] = BotClientId,
            ["azp"] = TeamsDesktopClientId,
        }, customize);

    /// <summary>What on-behalf-of returns: issued to the bot, for the downstream API.</summary>
    public string DownstreamToken(Action<Dictionary<string, object>>? customize = null) =>
        Mint(new()
        {
            ["aud"] = ApiClientId,
            ["azp"] = BotClientId,
        }, customize);

    /// <summary>Signs arbitrary claims. Set "exp" (unix seconds) to control expiry.</summary>
    public string Mint(Dictionary<string, object> claims, Action<Dictionary<string, object>>? customize = null, SecurityKey? key = null)
    {
        var all = new Dictionary<string, object>
        {
            ["ver"] = "2.0",
            ["iss"] = V2Issuer,
            ["tid"] = TenantId,
            ["oid"] = UserOid,
            ["name"] = "Adele Vance",
            ["preferred_username"] = "adele@contoso.com",
            ["scp"] = "access_as_user",
            ["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["nbf"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(),
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        };
        foreach (var (k, v) in claims) all[k] = v;
        customize?.Invoke(all);

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Claims = all,
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>Turns a token into a v1-shaped one (sts.windows.net issuer, App ID URI audience, upn/appid).</summary>
    public static Action<Dictionary<string, object>> AsV1(string audience) => c =>
    {
        c["ver"] = "1.0";
        c["iss"] = V1Issuer;
        c["aud"] = audience;
        if (c.Remove("preferred_username", out var upn)) c["upn"] = upn;
        if (c.Remove("azp", out var appId)) c["appid"] = appId;
    };

    public void Dispose() => _rsa.Dispose();
}
