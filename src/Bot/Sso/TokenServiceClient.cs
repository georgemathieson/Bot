using System.Net;
using Microsoft.Extensions.Options;
using Microsoft.Teams.Core;

namespace TeamsSsoBot.Sso;

/// <summary>
/// The Bot Framework token service calls the bot uses, wrapped so tests can stub them. The service is
/// backed by the OAuth connection on the Azure Bot resource.
/// </summary>
public interface ITokenServiceClient
{
    /// <summary>Sign-in link (button fallback) + tokenExchangeResource (SSO) for the OAuth connection.</summary>
    Task<GetSignInResourceResult> GetSignInResourceAsync(CardActionTurn turn, CancellationToken cancellationToken);

    /// <summary>Token stored for the user, or redeem a sign-in magic code. Null when there is none.</summary>
    Task<string?> GetUserTokenAsync(CardActionTurn turn, string? magicCode, CancellationToken cancellationToken);

    /// <summary>Ask the token service to OBO-exchange a Teams SSO token for the connection's scopes. Null if it can't.</summary>
    Task<string?> ExchangeTokenAsync(CardActionTurn turn, string ssoToken, CancellationToken cancellationToken);
}

public sealed class TokenServiceClient(IOptions<SsoOptions> options) : ITokenServiceClient
{
    private string ConnectionName => options.Value.OAuthConnectionName;

    public Task<GetSignInResourceResult> GetSignInResourceAsync(CardActionTurn turn, CancellationToken cancellationToken) =>
        Api(turn).GetSignInResourceAsync(BuildState(turn), cancellationToken: cancellationToken);

    public async Task<string?> GetUserTokenAsync(CardActionTurn turn, string? magicCode, CancellationToken cancellationToken)
    {
        try
        {
            // The SDK returns null on 404 (no token stored, or the magic code was not valid).
            var result = await Api(turn).GetAsync(turn.UserId, ConnectionName, turn.ChannelId, magicCode, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(result?.Token) ? null : result.Token;
        }
        catch (HttpRequestException ex) when (magicCode is not null && ex.StatusCode == HttpStatusCode.BadRequest)
        {
            // A malformed or expired magic code: let the user sign in again rather than failing the action.
            return null;
        }
    }

    public async Task<string?> ExchangeTokenAsync(CardActionTurn turn, string ssoToken, CancellationToken cancellationToken)
    {
        try
        {
            var result = await Api(turn).ExchangeAsync(turn.UserId, ConnectionName, turn.ChannelId, ssoToken, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(result?.Token) ? null : result.Token;
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            // The service could not exchange (typically consent is missing): fall back to interactive sign-in.
            return null;
        }
    }

    private static Microsoft.Teams.Apps.Clients.UserTokenApiClient Api(CardActionTurn turn) =>
        turn.TokenApi ?? throw new InvalidOperationException("CardActionTurn has no token service client; create it with CardActionTurn.FromContext.");

    private string BuildState(CardActionTurn turn)
    {
        // Same shape as the SDK's own OAuthFlow. MsAppId is what makes the token service include a
        // tokenExchangeResource (SSO); the conversation routes the sign-in result back.
        var state = new
        {
            ConnectionName,
            Conversation = new
            {
                turn.ActivityId,
                Bot = new { Id = turn.BotId },
                turn.ChannelId,
                Conversation = new { Id = turn.ConversationId },
                turn.ServiceUrl,
                User = new { Id = turn.UserId },
            },
            MsAppId = turn.AppId,
        };
        return Convert.ToBase64String(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(state));
    }
}
