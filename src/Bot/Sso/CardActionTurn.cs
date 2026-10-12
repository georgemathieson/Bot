using Microsoft.Agents.Builder;
using Microsoft.Agents.Core.Models;
using Microsoft.Agents.Core.Serialization;

namespace TeamsSsoBot.Sso;

/// <summary>What the SSO handshake needs to know about the current click.</summary>
/// <param name="Value">
/// The <c>adaptiveCard/action</c> invoke value. The Agents SDK models the SSO fields: <c>authentication.token</c>
/// (set by Teams after silent SSO) and <c>state</c> (the magic code after the fallback sign-in).
/// </param>
public sealed record CardActionTurn(
    string UserId,
    string ChannelId,
    string? AadObjectId,
    AdaptiveCardInvokeValue Value)
{
    /// <summary>The Agents SDK turn. The token service calls use it (null in unit tests).</summary>
    public ITurnContext? Context { get; init; }

    public string? Verb => Value.Action?.Verb;

    public static CardActionTurn FromContext(ITurnContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var activity = context.Activity;
        var value = activity.Value is null ? null : ProtocolJsonSerializer.ToObject<AdaptiveCardInvokeValue>(activity.Value);

        return new CardActionTurn(
            activity.From?.Id ?? string.Empty,
            (string?)activity.ChannelId ?? Channels.Msteams,
            activity.From?.AadObjectId,
            value ?? new AdaptiveCardInvokeValue())
        {
            Context = context,
        };
    }
}
