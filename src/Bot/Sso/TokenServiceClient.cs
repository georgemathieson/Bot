using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.UserAuth.TokenService;
using Microsoft.Agents.Core.Errors;
using Microsoft.Agents.Core.Models;
using Microsoft.Extensions.Options;

namespace TeamsSsoBot.Sso;

/// <summary>
/// The Bot Framework token service calls the bot uses, wrapped so tests can stub them. The service is
/// backed by the OAuth connection on the Azure Bot resource.
/// </summary>
public interface ITokenServiceClient
{
    /// <summary>Sign-in link (button fallback) + tokenExchangeResource (SSO) for the OAuth connection.</summary>
    Task<SignInResource> GetSignInResourceAsync(CardActionTurn turn, CancellationToken cancellationToken);

    /// <summary>Token stored for the user, or redeem a sign-in magic code. Null when there is none.</summary>
    Task<string?> GetUserTokenAsync(CardActionTurn turn, string? magicCode, CancellationToken cancellationToken);

    /// <summary>Ask the token service to OBO-exchange a Teams SSO token for the connection's scopes. Null if it can't.</summary>
    Task<string?> ExchangeTokenAsync(CardActionTurn turn, string ssoToken, CancellationToken cancellationToken);
}

/// <summary>
/// Backed by the Agents SDK's user token client for the turn. The SDK builds the sign-in state itself, including the
/// bot's app ID (<c>MsAppId</c>), which makes the token service return a <c>tokenExchangeResource</c> for SSO.
/// </summary>
public sealed class TokenServiceClient(IOptions<SsoOptions> options) : ITokenServiceClient
{
    private string ConnectionName => options.Value.OAuthConnectionName;

    public Task<SignInResource> GetSignInResourceAsync(CardActionTurn turn, CancellationToken cancellationToken) =>
        UserTokenClientWrapper.GetSignInResourceAsync(Context(turn), ConnectionName, cancellationToken);

    public async Task<string?> GetUserTokenAsync(CardActionTurn turn, string? magicCode, CancellationToken cancellationToken)
    {
        try
        {
            // The SDK returns null on 404 (no token stored, or the magic code was not valid). Its parameter is not
            // annotated as nullable, but a null magic code means "the stored token", as in the token service API.
            var result = await UserTokenClientWrapper.GetUserTokenAsync(Context(turn), ConnectionName, magicCode!, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(result?.Token) ? null : result.Token;
        }
        catch (ErrorResponseException) when (magicCode is not null)
        {
            // A malformed or expired magic code: let the user sign in again rather than failing the action.
            return null;
        }
    }

    public async Task<string?> ExchangeTokenAsync(CardActionTurn turn, string ssoToken, CancellationToken cancellationToken)
    {
        try
        {
            var result = await UserTokenClientWrapper.ExchangeTokenAsync(
                Context(turn), ConnectionName, new TokenExchangeRequest { Token = ssoToken }, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(result?.Token) ? null : result.Token;
        }
        catch (ErrorResponseException)
        {
            // 400/412 and similar: the token service can't exchange this token without the user (consent is
            // usually missing). The caller falls back to the Sign in button.
            return null;
        }
    }

    /// <summary>Removes the user's token for the OAuth connection from the token store.</summary>
    public Task SignOutAsync(ITurnContext context, CancellationToken cancellationToken) =>
        UserTokenClientWrapper.SignOutUserAsync(context, ConnectionName, cancellationToken);

    private static ITurnContext Context(CardActionTurn turn) =>
        turn.Context ?? throw new InvalidOperationException("CardActionTurn has no turn context; create it with CardActionTurn.FromContext.");
}
