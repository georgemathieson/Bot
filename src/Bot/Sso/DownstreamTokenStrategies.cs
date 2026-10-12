using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;

namespace TeamsSsoBot.Sso;

/// <summary>How the downstream token was obtained. Shown on the result card so you can see which path ran.</summary>
public enum UserTokenSource
{
    SsoThenObo,
    SsoThenTokenService,
    TokenServiceCache,
    SignInFallback,
}

/// <summary>The exchange can only succeed after the user interacts (consents, completes MFA, ...).</summary>
public sealed class InteractionRequiredException(string message, string? claims = null) : Exception(message)
{
    /// <summary>Claims challenge from Entra (Conditional Access), if any.</summary>
    public string? Claims { get; } = claims;
}

/// <summary>Turns the Teams SSO token (aud = the SSO app) into a token for the downstream API (aud = the API).</summary>
public interface IDownstreamTokenStrategy
{
    /// <summary>A downstream token that is already available without asking Teams for SSO, if any.</summary>
    Task<string?> GetCachedTokenAsync(CardActionTurn turn, CancellationToken cancellationToken);

    /// <summary>
    /// Exchanges the SSO token. Throws <see cref="InteractionRequiredException"/> when the user has to see a
    /// prompt (consent, MFA / Conditional Access step-up). Any other exception is a real failure.
    /// </summary>
    Task<(string AccessToken, UserTokenSource Source)> ExchangeSsoTokenAsync(CardActionTurn turn, string ssoToken, CancellationToken cancellationToken);
}

/// <summary>Acquires an access token on behalf of the user represented by an assertion (the SSO token).</summary>
public interface IOboTokenAcquirer
{
    Task<string> AcquireTokenOnBehalfOfAsync(string assertion, IEnumerable<string> scopes, CancellationToken cancellationToken);
}

/// <summary>
/// MSAL.NET implementation of the OAuth 2.0 on-behalf-of grant, made by the SSO app registration:
/// <code>
/// POST https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token
///   grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer  requested_token_use=on_behalf_of
///   assertion=&lt;Teams SSO token&gt;  client_id=&lt;SSO app&gt;  scope=&lt;downstream scopes&gt;
///   client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer
///   client_assertion=&lt;managed identity token for api://AzureADTokenExchange&gt;
/// </code>
/// (or <c>client_secret</c> when the bot runs on your computer). Register as a singleton: MSAL's in-memory cache is
/// keyed by the assertion, so repeated clicks carrying the same (still valid) SSO token don't hit Entra again.
/// For several instances, add a distributed cache.
/// </summary>
public sealed class MsalOboTokenAcquirer : IOboTokenAcquirer
{
    private readonly IConfidentialClientApplication _msal;

    public MsalOboTokenAcquirer(IOptions<SsoAppOptions> ssoApp, IMsalHttpClientFactory? httpClientFactory = null)
    {
        var app = ssoApp.Value;
        var builder = ConfidentialClientApplicationBuilder.Create(app.ClientId)
            .WithAuthority($"{app.Instance.TrimEnd('/')}/{app.TenantId}")
            .WithSsoAppCredential(app, httpClientFactory);
        if (httpClientFactory is not null)
        {
            builder.WithHttpClientFactory(httpClientFactory);
        }
        _msal = builder.Build();
    }

    public async Task<string> AcquireTokenOnBehalfOfAsync(string assertion, IEnumerable<string> scopes, CancellationToken cancellationToken)
    {
        var result = await _msal.AcquireTokenOnBehalfOf(scopes, new UserAssertion(assertion))
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);
        return result.AccessToken;
    }
}

/// <summary>
/// Option A (default): the bot runs on-behalf-of itself. Stateless: nothing is stored in the Bot Framework
/// token store, every click goes through the (silent) SSO handshake, and one SSO token can be exchanged for
/// any number of downstream APIs.
/// </summary>
public sealed class OboTokenStrategy(IOboTokenAcquirer acquirer, IOptions<DownstreamApiOptions> downstream) : IDownstreamTokenStrategy
{
    // Entra errors a user can fix by signing in interactively. MSAL already throws MsalUiRequiredException
    // for most of these; the codes are a belt-and-braces check on the message.
    private static readonly string[] InteractiveErrorCodes =
    [
        "AADSTS65001", // consent required
        "AADSTS50076", // MFA required
        "AADSTS50079", // MFA registration required
        "AADSTS50158", // external security challenge
        "AADSTS50173", // grant revoked, re-sign-in needed
    ];

    public Task<string?> GetCachedTokenAsync(CardActionTurn turn, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    public async Task<(string AccessToken, UserTokenSource Source)> ExchangeSsoTokenAsync(CardActionTurn turn, string ssoToken, CancellationToken cancellationToken)
    {
        try
        {
            var token = await acquirer.AcquireTokenOnBehalfOfAsync(ssoToken, downstream.Value.Scopes, cancellationToken).ConfigureAwait(false);
            return (token, UserTokenSource.SsoThenObo);
        }
        catch (MsalUiRequiredException ex)
        {
            throw new InteractionRequiredException($"{ex.ErrorCode}: {ex.Message}", ex.Claims);
        }
        catch (MsalServiceException ex) when (InteractiveErrorCodes.Any(code => ex.Message.Contains(code, StringComparison.Ordinal)))
        {
            throw new InteractionRequiredException($"{ex.ErrorCode}: {ex.Message}", ex.Claims);
        }
    }
}

/// <summary>
/// Option B: the Bot Framework token service runs the OBO exchange with the Azure Bot's OAuth connection
/// (the SSO app's client ID and credential, Token Exchange URL and Scopes), then stores and refreshes the token,
/// so later clicks skip the SSO round trip.
/// </summary>
public sealed class TokenServiceTokenStrategy(ITokenServiceClient tokenService) : IDownstreamTokenStrategy
{
    public Task<string?> GetCachedTokenAsync(CardActionTurn turn, CancellationToken cancellationToken) =>
        tokenService.GetUserTokenAsync(turn, magicCode: null, cancellationToken);

    public async Task<(string AccessToken, UserTokenSource Source)> ExchangeSsoTokenAsync(CardActionTurn turn, string ssoToken, CancellationToken cancellationToken)
    {
        var token = await tokenService.ExchangeTokenAsync(turn, ssoToken, cancellationToken).ConfigureAwait(false)
            ?? throw new InteractionRequiredException("The token service could not exchange the SSO token (consent is usually missing).");
        return (token, UserTokenSource.SsoThenTokenService);
    }
}
