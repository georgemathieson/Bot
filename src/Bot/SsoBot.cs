using System.Text.RegularExpressions;
using Microsoft.Agents.Builder;
using Microsoft.Agents.Builder.App;
using Microsoft.Agents.Builder.State;
using Microsoft.Agents.Core.Models;
using TeamsSsoBot.Sso;

namespace TeamsSsoBot;

/// <summary>
/// The agent (Microsoft 365 Agents SDK). It sends the welcome card, handles "signout", and runs every Adaptive Card
/// <c>Action.Execute</c> click through <see cref="CardActionHandlers"/>.
/// </summary>
public sealed class SsoBot : AgentApplication
{
    private readonly TokenServiceClient _tokenService;

    public SsoBot(AgentApplicationOptions options, CardActionHandlers handlers, TokenServiceClient tokenService) : base(options)
    {
        _tokenService = tokenService;

        OnActivity(ActivityTypes.InstallationUpdate, WelcomeOnInstallAsync);
        OnActivity(ActivityTypes.Message, OnMessageAsync);

        // All verbs: CardActionHandlers answers unknown verbs with a 400 instead of starting a sign-in.
        // The SDK sends the returned AdaptiveCardInvokeResponse as the HTTP 200 body of the invoke.
        AdaptiveCards.OnActionExecute(new Regex(".*"), (turnContext, _, _, cancellationToken) =>
            handlers.OnCardActionAsync(CardActionTurn.FromContext(turnContext), cancellationToken));
    }

    private static Task WelcomeOnInstallAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken) =>
        turnContext.Activity.Action == "add"
            ? SendWelcomeAsync(turnContext, cancellationToken)
            : Task.CompletedTask;

    private async Task OnMessageAsync(ITurnContext turnContext, ITurnState turnState, CancellationToken cancellationToken)
    {
        if (string.Equals(turnContext.Activity.Text?.Trim(), "signout", StringComparison.OrdinalIgnoreCase))
        {
            // Only the token-service strategy and the sign-in fallback store tokens in the token store.
            await _tokenService.SignOutAsync(turnContext, cancellationToken).ConfigureAwait(false);
            await turnContext.SendActivityAsync("Signed out: any token cached in the Bot Framework token store was removed.", cancellationToken: cancellationToken).ConfigureAwait(false);
            return;
        }
        await SendWelcomeAsync(turnContext, cancellationToken).ConfigureAwait(false);
    }

    private static Task SendWelcomeAsync(ITurnContext turnContext, CancellationToken cancellationToken) =>
        turnContext.SendActivityAsync(MessageFactory.Attachment(new Attachment
        {
            ContentType = ContentTypes.AdaptiveCard,
            Content = Cards.Welcome(),
        }), cancellationToken);
}
