using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace TeamsSsoBot.Sso;

/// <summary>The claims of a validated Teams SSO token that the bot and the result card use.</summary>
public sealed record SsoTokenClaims(string? Audience, string? Scope, string? AuthorizedParty, string? ObjectId, string? Version);

public sealed class SsoTokenValidationException(string message) : Exception(message);

public interface ISsoTokenValidator
{
    /// <summary>Validates the Teams SSO token and that it belongs to <paramref name="expectedUserObjectId"/>.</summary>
    Task<SsoTokenClaims> ValidateAsync(string token, string? expectedUserObjectId, CancellationToken cancellationToken);
}

/// <summary>
/// Validates the token Teams hands back after a silent SSO handshake.
/// <para>
/// It is a real Entra access token: issued to the Teams client (azp = Teams' client ID) for the SSO app's
/// API (aud = SSO app client ID or api://botid-...), with scp = access_as_user. It is the OBO assertion.
/// Beyond signature, issuer, audience and scope, we check it belongs to the user who clicked
/// (oid == activity.from.aadObjectId), so a token can't be replayed on someone else's activity.
/// </para>
/// </summary>
public sealed class SsoTokenValidator : ISsoTokenValidator
{
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _metadata;
    private readonly string _tenantId;
    private readonly string _requiredScope;
    private readonly TokenValidationParameters _parameters;
    private readonly JsonWebTokenHandler _handler = new();

    public SsoTokenValidator(
        IOptions<SsoAppOptions> ssoApp,
        IOptions<SsoOptions> sso,
        IConfigurationManager<OpenIdConnectConfiguration>? metadata = null)
    {
        var id = ssoApp.Value;
        var instance = id.Instance.TrimEnd('/');
        _tenantId = id.TenantId;
        _requiredScope = sso.Value.BotSsoScope;
        _metadata = metadata ?? new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{instance}/{id.TenantId}/v2.0/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever());

        _parameters = new TokenValidationParameters
        {
            ValidIssuers = [$"{instance}/{id.TenantId}/v2.0", $"https://sts.windows.net/{id.TenantId}/"],
            ValidAudiences = [id.ClientId, sso.Value.BotAppIdUri],
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    }

    public async Task<SsoTokenClaims> ValidateAsync(string token, string? expectedUserObjectId, CancellationToken cancellationToken)
    {
        var result = await ValidateSignatureAndClaimsAsync(token, cancellationToken).ConfigureAwait(false);
        if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // Entra rotated its signing keys since we cached the metadata: refresh once and retry.
            _metadata.RequestRefresh();
            result = await ValidateSignatureAndClaimsAsync(token, cancellationToken).ConfigureAwait(false);
        }
        if (!result.IsValid)
        {
            throw new SsoTokenValidationException(result.Exception?.Message ?? "Token is invalid");
        }

        var jwt = (JsonWebToken)result.SecurityToken;
        string? Claim(string name) => jwt.TryGetPayloadValue(name, out string value) ? value : null;

        if (Claim("tid") != _tenantId)
        {
            throw new SsoTokenValidationException($"Token is for tenant {Claim("tid")}, expected {_tenantId}");
        }

        var scopes = (Claim("scp") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains(_requiredScope, StringComparer.Ordinal))
        {
            throw new SsoTokenValidationException($"Token is missing the {_requiredScope} scope");
        }

        if (!string.IsNullOrEmpty(expectedUserObjectId) && Claim("oid") != expectedUserObjectId)
        {
            throw new SsoTokenValidationException("SSO token was issued to a different user than the one who sent the activity");
        }

        return new SsoTokenClaims(
            Audience: jwt.Audiences.FirstOrDefault(),
            Scope: Claim("scp"),
            AuthorizedParty: Claim("azp") ?? Claim("appid"),
            ObjectId: Claim("oid"),
            Version: Claim("ver"));
    }

    private async Task<TokenValidationResult> ValidateSignatureAndClaimsAsync(string token, CancellationToken cancellationToken)
    {
        var config = await _metadata.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var parameters = _parameters.Clone();
        parameters.IssuerSigningKeys = config.SigningKeys;
        return await _handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
    }
}
