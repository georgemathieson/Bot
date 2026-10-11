using TeamsSsoBot.Sso;

namespace TeamsSsoBot;

/// <summary>Adaptive Cards for the demo. Universal Actions (Action.Execute) need card version 1.4+.</summary>
public static class Cards
{
    public const string CallApiVerb = "callApi";

    private static readonly Dictionary<UserTokenSource, string> SourceLabels = new()
    {
        [UserTokenSource.SsoThenObo] = "Teams SSO, then the bot ran on-behalf-of with MSAL",
        [UserTokenSource.SsoThenTokenService] = "Teams SSO, then the Bot Framework token service ran the OBO exchange",
        [UserTokenSource.TokenServiceCache] = "Cached in the Bot Framework token store",
        [UserTokenSource.SignInFallback] = "Interactive sign-in fallback (consent / MFA was needed)",
    };

    private static readonly Dictionary<string, string> TeamsClients = new()
    {
        ["1fec8e78-bce4-4aaf-ab1b-5451cc387264"] = "Teams desktop/mobile",
        ["5e3ce6c0-2b1f-4285-8d4b-75ee78787346"] = "Teams web",
    };

    private static object CallApiAction(string title) => new
    {
        type = "Action.Execute",
        title,
        verb = CallApiVerb,
        data = new { action = CallApiVerb },
    };

    public static object Welcome() => new
    {
        type = "AdaptiveCard",
        version = "1.5",
        body = new object[]
        {
            new { type = "TextBlock", text = "Teams SSO + on-behalf-of demo", size = "Large", weight = "Bolder", wrap = true },
            new
            {
                type = "TextBlock",
                wrap = true,
                text = "Select the button. The bot asks Teams for a single sign-on token, exchanges it on your behalf for a " +
                       "token to the downstream API, and calls the API as you. If consent is already granted, you see no popup.",
            },
        },
        actions = new[] { CallApiAction("Call downstream API") },
    };

    public static object Result(DownstreamResult result, UserToken token, bool showDiagnostics)
    {
        var body = new List<object>
        {
            new { type = "TextBlock", text = "Downstream API call succeeded", size = "Large", weight = "Bolder", color = "Good", wrap = true },
            new { type = "TextBlock", text = result.Message, wrap = true },
            FactSet(
                ("User", result.User.Name),
                ("Username", result.User.Username),
                ("Object ID", result.User.Oid),
                ("Token obtained via", SourceLabels[token.Source])),
        };

        if (showDiagnostics)
        {
            body.Add(Heading("Downstream token (as the API saw it)"));
            body.Add(FactSet(
                ("aud", result.Token.Aud),
                ("scp", result.Token.Scp),
                ("azp/appid (caller)", result.Token.CallingAppId),
                ("ver", result.Token.Ver),
                ("expires", result.Token.ExpiresAt?.ToString("u"))));

            if (token.SsoClaims is { } sso)
            {
                var caller = sso.AuthorizedParty is { } azp && TeamsClients.TryGetValue(azp, out var client) ? $"{azp} ({client})" : sso.AuthorizedParty;
                body.Add(Heading("Teams SSO token (sent to the bot)"));
                body.Add(FactSet(("aud", sso.Audience), ("scp", sso.Scope), ("azp/appid (caller)", caller), ("ver", sso.Version)));
            }
        }

        return new
        {
            type = "AdaptiveCard",
            version = "1.5",
            body,
            actions = new[] { CallApiAction("Call again") },
        };
    }

    public static object Error(string title, string detail) => new
    {
        type = "AdaptiveCard",
        version = "1.5",
        body = new object[]
        {
            new { type = "TextBlock", text = title, size = "Large", weight = "Bolder", color = "Attention", wrap = true },
            new { type = "TextBlock", text = detail, wrap = true, fontType = "Monospace", size = "Small" },
        },
        actions = new[] { CallApiAction("Try again") },
    };

    private static object Heading(string text) => new { type = "TextBlock", text, weight = "Bolder", wrap = true, spacing = "Medium" };

    private static object FactSet(params (string Title, string? Value)[] facts) => new
    {
        type = "FactSet",
        facts = facts.Select(f => new { title = f.Title, value = string.IsNullOrEmpty(f.Value) ? "-" : f.Value }).ToArray(),
    };
}
