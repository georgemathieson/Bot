using Microsoft.Extensions.Options;
using Microsoft.Teams.Apps;

namespace TeamsSsoBot.Sso;

/// <summary>A token for the downstream API, obtained for the user who clicked.</summary>
/// <param name="AccessToken">Never log or display it.</param>
/// <param name="Source">Which path produced it.</param>
/// <param name="SsoClaims">Claims of the Teams SSO token, when this click went through SSO.</param>
public sealed record UserToken(string AccessToken, UserTokenSource Source, SsoTokenClaims? SsoClaims = null);

public delegate Task<InvokeResponse> AuthenticatedCardActionHandler(CardActionTurn turn, UserToken token, CancellationToken cancellationToken);

/// <summary>
/// Runs an Adaptive Card <c>Action.Execute</c> handler with a token for the downstream API, obtained
/// <em>silently</em> through Teams SSO + on-behalf-of. This is the Universal Actions auth protocol:
/// <list type="number">
/// <item>Click: invoke <c>adaptiveCard/action</c> arrives with no token.</item>
/// <item>Bot: replies 401 <c>application/vnd.microsoft.activity.loginRequest</c> carrying a <c>tokenExchangeResource</c>.</item>
/// <item>Teams: silently gets an Entra token for the bot's API (<c>api://botid-...</c>), with no popup when the Teams
/// clients are pre-authorized and consent is granted, and re-sends the same invoke with <c>value.authentication.token</c>.</item>
/// <item>Bot: validates that token, exchanges it on behalf of the user for a downstream token, runs the handler,
/// and returns the updated card.</item>
/// </list>
/// Fallbacks: if the exchange needs user interaction (consent / MFA), the bot replies with a loginRequest
/// <em>without</em> SSO info, so Teams shows a Sign in button; once the user signs in, Teams re-sends the invoke with
/// the magic code in <c>value.state</c>. If the SSO token itself is unusable, the bot replies 412, which also makes
/// Teams fall back to the Sign in button.
/// </summary>
public sealed class CardActionSso(
    ITokenServiceClient tokenService,
    IDownstreamTokenStrategy strategy,
    ISsoTokenValidator validator,
    IOptions<SsoOptions> options,
    ILogger<CardActionSso> logger)
{
    private readonly SsoOptions _options = options.Value;

    public async Task<InvokeResponse> HandleAsync(CardActionTurn turn, AuthenticatedCardActionHandler handler, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(handler);
        var value = turn.Value;

        // Back from the nominal (button) sign-in: Teams sends the magic code in "state".
        if (!string.IsNullOrEmpty(value.State))
        {
            var signedInToken = await tokenService.GetUserTokenAsync(turn, value.State, cancellationToken).ConfigureAwait(false);
            if (signedInToken is null)
            {
                logger.LogWarning("Sign-in magic code could not be redeemed; asking the user to sign in again.");
                return await LoginRequestAsync(turn, withSso: false, cancellationToken).ConfigureAwait(false);
            }
            return await handler(turn, new UserToken(signedInToken, UserTokenSource.SignInFallback), cancellationToken).ConfigureAwait(false);
        }

        // Teams completed silent SSO and re-sent the action with the token.
        var ssoToken = value.Authentication?.Token;
        if (!string.IsNullOrEmpty(ssoToken))
        {
            SsoTokenClaims ssoClaims;
            try
            {
                ssoClaims = await validator.ValidateAsync(ssoToken, turn.AadObjectId, cancellationToken).ConfigureAwait(false);
            }
            catch (SsoTokenValidationException ex)
            {
                logger.LogWarning("Rejected SSO token: {Reason}", ex.Message);
                return CardActionResponses.PreconditionFailed("The single sign-on token could not be validated");
            }

            string accessToken;
            UserTokenSource source;
            try
            {
                (accessToken, source) = await strategy.ExchangeSsoTokenAsync(turn, ssoToken, cancellationToken).ConfigureAwait(false);
            }
            catch (InteractionRequiredException ex)
            {
                logger.LogInformation("Silent exchange needs user interaction, falling back to sign-in: {Reason}", ex.Message);
                return await LoginRequestAsync(turn, withSso: false, cancellationToken).ConfigureAwait(false);
            }
            return await handler(turn, new UserToken(accessToken, source, ssoClaims), cancellationToken).ConfigureAwait(false);
        }

        // Already have a downstream token (token-service strategy only).
        var cached = await strategy.GetCachedTokenAsync(turn, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return await handler(turn, new UserToken(cached, UserTokenSource.TokenServiceCache), cancellationToken).ConfigureAwait(false);
        }

        // No token yet: ask Teams to perform silent SSO.
        return await LoginRequestAsync(turn, withSso: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<InvokeResponse> LoginRequestAsync(CardActionTurn turn, bool withSso, CancellationToken cancellationToken)
    {
        var resource = await tokenService.GetSignInResourceAsync(turn, cancellationToken).ConfigureAwait(false);
        TokenExchangeResourceBody? exchangeResource = null;

        if (withSso)
        {
            var botAppIdUri = _options.BotAppIdUri!;
            // OriginalString, not ToString(): ToString() would append a trailing slash to "api://botid-...".
            var connectionUri = resource.TokenExchangeResource?.Uri?.OriginalString;
            if (string.IsNullOrEmpty(connectionUri))
            {
                logger.LogWarning(
                    "OAuth connection '{Connection}' returned no tokenExchangeResource - set its Token Exchange URL to {Uri}. " +
                    "Using the configured BotAppIdUri so SSO can still be attempted.",
                    _options.OAuthConnectionName, botAppIdUri);
            }
            else if (!string.Equals(connectionUri, botAppIdUri, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "OAuth connection Token Exchange URL ({ConnectionUri}) differs from BotAppIdUri ({BotAppIdUri}); " +
                    "Teams will request a token for the connection's value.",
                    connectionUri, botAppIdUri);
            }

            exchangeResource = new TokenExchangeResourceBody(
                resource.TokenExchangeResource?.Id ?? Guid.NewGuid().ToString(),
                string.IsNullOrEmpty(connectionUri) ? botAppIdUri : connectionUri,
                resource.TokenExchangeResource?.ProviderId);
        }

        return CardActionResponses.LoginRequest(_options.OAuthConnectionName, resource.SignInLink, exchangeResource);
    }
}
