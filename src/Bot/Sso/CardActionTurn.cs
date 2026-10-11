using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Clients;

namespace TeamsSsoBot.Sso;

/// <summary>
/// The <c>value</c> of an <c>adaptiveCard/action</c> invoke, including the SSO fields the SDK's own
/// <see cref="AdaptiveCardActionValue"/> doesn't model (<c>authentication</c>).
/// </summary>
public sealed class CardActionInvokeValue
{
    [JsonPropertyName("action")]
    public AdaptiveCardAction? Action { get; set; }

    /// <summary>Set by Teams when it re-sends the action after a silent SSO handshake.</summary>
    [JsonPropertyName("authentication")]
    public CardActionAuthentication? Authentication { get; set; }

    /// <summary>Magic code, set by Teams after the user completed the fallback sign-in.</summary>
    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("trigger")]
    public string? Trigger { get; set; }
}

public sealed class CardActionAuthentication
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("connectionName")]
    public string? ConnectionName { get; set; }

    /// <summary>The Teams SSO token: an Entra access token for the bot's API (aud = the bot).</summary>
    [JsonPropertyName("token")]
    public string? Token { get; set; }
}

/// <summary>What the SSO handshake needs to know about the current click, decoupled from the SDK context.</summary>
public sealed record CardActionTurn(
    string UserId,
    string ChannelId,
    string? AadObjectId,
    CardActionInvokeValue Value)
{
    /// <summary>The SDK's Bot Framework token service client for this turn (null in unit tests).</summary>
    public UserTokenApiClient? TokenApi { get; init; }

    /// <summary>Conversation details the token service needs in the sign-in state.</summary>
    public string? AppId { get; init; }
    public string? ActivityId { get; init; }
    public string? BotId { get; init; }
    public string? ConversationId { get; init; }
    public string? ServiceUrl { get; init; }

    public string? Verb => Value.Action?.Verb;

    public static CardActionTurn FromContext(Context<InvokeActivity<AdaptiveCardActionValue>> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        // InvokeActivity keeps the raw JSON; read it ourselves to get the "authentication" field.
        var raw = ((InvokeActivity)context.Activity).Value;
        var value = raw?.Deserialize<CardActionInvokeValue>() ?? new CardActionInvokeValue();

        return new CardActionTurn(
            context.Activity.From?.Id ?? string.Empty,
            context.Activity.ChannelId ?? "msteams",
            context.Activity.From?.AadObjectId,
            value)
        {
            TokenApi = context.Api.UserToken,
            AppId = context.AppId,
            ActivityId = context.Activity.Id,
            BotId = context.Activity.Recipient?.Id,
            ConversationId = context.Activity.Conversation?.Id,
            ServiceUrl = context.Activity.ServiceUrl?.ToString(),
        };
    }
}
