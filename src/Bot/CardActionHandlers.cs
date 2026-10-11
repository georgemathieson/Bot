using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Teams.Apps;
using TeamsSsoBot.Sso;

namespace TeamsSsoBot;

/// <summary>Handles Action.Execute clicks: the "callApi" verb runs through the SSO handshake, then calls the API.</summary>
public sealed class CardActionHandlers(
    CardActionSso sso,
    DownstreamApiClient downstreamApi,
    IOptions<SsoOptions> options,
    ILogger<CardActionHandlers> logger)
{
    private readonly bool _showDiagnostics = options.Value.ShowDiagnostics;

    public async Task<InvokeResponse> OnCardActionAsync(CardActionTurn turn, CancellationToken cancellationToken)
    {
        if (turn.Verb != Cards.CallApiVerb)
        {
            return CardActionResponses.Error(400, "BadRequest", $"Unknown card action \"{turn.Verb}\"");
        }

        try
        {
            return await sso.HandleAsync(turn, CallDownstreamApiAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not an "ask the user" situation: usually configuration (wrong secret, scope, audience...).
            logger.LogError(ex, "Could not obtain a token for the downstream API");
            return CardActionResponses.Card(Cards.Error("Could not get a token for the downstream API", Detail(ex)));
        }
    }

    private async Task<InvokeResponse> CallDownstreamApiAsync(CardActionTurn turn, UserToken token, CancellationToken cancellationToken)
    {
        try
        {
            var result = await downstreamApi.GetMeAsync(token.AccessToken, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Downstream API accepted the token for {User} ({Source})", result.User.Username ?? result.User.Oid, token.Source);
            return CardActionResponses.Card(Cards.Result(result, token, _showDiagnostics));
        }
        catch (Exception ex) when (ex is DownstreamApiException or HttpRequestException)
        {
            logger.LogError(ex, "Downstream API call failed");
            return CardActionResponses.Card(Cards.Error("The downstream API call failed", Detail(ex)));
        }
    }

    /// <summary>Error text for the card. Entra messages carry the AADSTS code, which is what you need to debug.</summary>
    private string Detail(Exception ex)
    {
        if (!_showDiagnostics) return "Ask your administrator to check the bot logs.";
        return ex switch
        {
            MsalException msal => $"{msal.ErrorCode}: {msal.Message}",
            HttpRequestException { StatusCode: { } status } http => $"HTTP {(int)status}: {http.Message}",
            _ => ex.Message,
        };
    }
}
