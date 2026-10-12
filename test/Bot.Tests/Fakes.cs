using Microsoft.Teams.Core;
using TeamsSsoBot.Sso;
using TestSupport;

namespace Bot.Tests;

internal static class Turns
{
    public static CardActionTurn Click(string verb = "callApi", CardActionAuthentication? authentication = null, string? state = null) =>
        new("29:user-1", "msteams", TestTenant.UserOid, new CardActionInvokeValue
        {
            Action = new() { Type = "Action.Execute", Verb = verb, Data = new() { ["action"] = verb } },
            Authentication = authentication,
            State = state,
            Trigger = "manual",
        });

    public static CardActionAuthentication Sso(string token) => new() { Id = "ter-1", ConnectionName = "sso", Token = token };
}

internal sealed class FakeTokenService : ITokenServiceClient
{
    public const string SignInLink = "https://token.botframework.com/api/oauth/signin?signin=abc";

    public GetSignInResourceResult Resource { get; set; } = new()
    {
        SignInLink = SignInLink,
        TokenExchangeResource = new TokenExchangeResource { Id = "ter-1", Uri = new Uri(TestTenant.BotAppIdUri), ProviderId = "aad-provider" },
    };

    public Dictionary<string, string> TokensByMagicCode { get; } = [];
    public string? StoredToken { get; set; }
    public string? ExchangeResult { get; set; }
    public int SignInResourceCalls { get; private set; }
    public List<string?> GetUserTokenCodes { get; } = [];
    public List<string> ExchangedTokens { get; } = [];

    public Task<GetSignInResourceResult> GetSignInResourceAsync(CardActionTurn turn, CancellationToken cancellationToken)
    {
        SignInResourceCalls++;
        return Task.FromResult(Resource);
    }

    public Task<string?> GetUserTokenAsync(CardActionTurn turn, string? magicCode, CancellationToken cancellationToken)
    {
        GetUserTokenCodes.Add(magicCode);
        return Task.FromResult(magicCode is null ? StoredToken : TokensByMagicCode.GetValueOrDefault(magicCode));
    }

    public Task<string?> ExchangeTokenAsync(CardActionTurn turn, string ssoToken, CancellationToken cancellationToken)
    {
        ExchangedTokens.Add(ssoToken);
        return Task.FromResult(ExchangeResult);
    }
}

internal sealed class FakeStrategy : IDownstreamTokenStrategy
{
    public string? Cached { get; set; }
    public Func<string, (string, UserTokenSource)> Exchange { get; set; } = _ => ("downstream-token", UserTokenSource.SsoThenObo);
    public List<string> ExchangedTokens { get; } = [];

    public Task<string?> GetCachedTokenAsync(CardActionTurn turn, CancellationToken cancellationToken) => Task.FromResult(Cached);

    public Task<(string AccessToken, UserTokenSource Source)> ExchangeSsoTokenAsync(CardActionTurn turn, string ssoToken, CancellationToken cancellationToken)
    {
        ExchangedTokens.Add(ssoToken);
        return Task.FromResult(Exchange(ssoToken));
    }
}

internal sealed class FakeValidator : ISsoTokenValidator
{
    public static readonly SsoTokenClaims Claims = new(TestTenant.SsoAppClientId, "access_as_user", TestTenant.TeamsDesktopClientId, TestTenant.UserOid, "2.0");

    public string? RejectWith { get; set; }
    public List<(string Token, string? Oid)> Calls { get; } = [];

    public Task<SsoTokenClaims> ValidateAsync(string token, string? expectedUserObjectId, CancellationToken cancellationToken)
    {
        Calls.Add((token, expectedUserObjectId));
        return RejectWith is null ? Task.FromResult(Claims) : throw new SsoTokenValidationException(RejectWith);
    }
}
