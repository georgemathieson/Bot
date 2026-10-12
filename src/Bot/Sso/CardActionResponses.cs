using Microsoft.Agents.Builder.App.AdaptiveCards;
using Microsoft.Agents.Core.Models;

namespace TeamsSsoBot.Sso;

/// <summary>
/// Universal Actions invoke response bodies, built with the Agents SDK's
/// <see cref="AdaptiveCardInvokeResponseFactory"/>. The SDK sends them as HTTP 200, like Microsoft's C# SSO sample:
/// the status Teams acts on is the <c>statusCode</c> inside the body.
/// </summary>
public static class CardActionResponses
{
    public const string ErrorType = "application/vnd.microsoft.error";

    /// <summary>
    /// 401 loginRequest. With a <paramref name="tokenExchangeResource"/>, Teams attempts silent SSO and
    /// re-sends the action with <c>value.authentication.token</c>; without one, it shows a Sign in button.
    /// </summary>
    public static AdaptiveCardInvokeResponse LoginRequest(string connectionName, string? signInLink, TokenExchangeResource? tokenExchangeResource, string text = "Sign in to continue") =>
        AdaptiveCardInvokeResponseFactory.Login(new OAuthCard
        {
            Text = text,
            ConnectionName = connectionName,
            TokenExchangeResource = tokenExchangeResource,
            Buttons = [new CardAction { Type = ActionTypes.Signin, Title = "Sign in", Text = "Sign in", Value = signInLink }],
        });

    /// <summary>412: the SSO token can't be used. Teams falls back to the Sign in button.</summary>
    public static AdaptiveCardInvokeResponse PreconditionFailed(string message) =>
        AdaptiveCardInvokeResponseFactory.PreConditionFailed(message);

    public static AdaptiveCardInvokeResponse Error(int statusCode, string code, string message) => new()
    {
        StatusCode = statusCode,
        Type = ErrorType,
        Value = new Error { Code = code, Message = message },
    };

    /// <summary>200: replace the card with <paramref name="card"/>.</summary>
    public static AdaptiveCardInvokeResponse Card(object card) => new()
    {
        StatusCode = 200,
        Type = ContentTypes.AdaptiveCard,
        Value = card,
    };
}
