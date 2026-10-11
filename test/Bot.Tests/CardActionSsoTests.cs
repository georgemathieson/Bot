using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Teams.Apps;
using TeamsSsoBot.Sso;
using TestSupport;

namespace Bot.Tests;

/// <summary>The Action.Execute SSO handshake, with the token service, validator and strategy faked.</summary>
public sealed class CardActionSsoTests
{
    private readonly FakeTokenService _tokenService = new();
    private readonly FakeStrategy _strategy = new();
    private readonly FakeValidator _validator = new();
    private readonly List<(CardActionTurn Turn, UserToken Token)> _handled = [];

    private CardActionSso Sso() => new(
        _tokenService,
        _strategy,
        _validator,
        Options.Create(new SsoOptions { OAuthConnectionName = "sso", BotAppIdUri = TestTenant.BotAppIdUri }),
        NullLogger<CardActionSso>.Instance);

    private Task<InvokeResponse> Handled(CardActionTurn turn, UserToken token, CancellationToken _)
    {
        _handled.Add((turn, token));
        return Task.FromResult(CardActionResponses.Card(new { type = "AdaptiveCard" }));
    }

    private async Task<JsonNode> Run(CardActionTurn turn)
    {
        var response = await Sso().HandleAsync(turn, Handled, TestContext.Current.CancellationToken);
        Assert.Equal(200, response.Status); // invoke responses always travel as HTTP 200; the body has the real status
        return JsonSerializer.SerializeToNode(response.Body)!;
    }

    [Fact]
    public async Task First_click_replies_401_loginRequest_with_tokenExchangeResource()
    {
        var body = await Run(Turns.Click());

        Assert.Empty(_handled);
        Assert.Equal(401, (int)body["statusCode"]!);
        Assert.Equal("application/vnd.microsoft.activity.loginRequest", (string)body["type"]!);
        var card = body["value"]!;
        Assert.Equal("sso", (string)card["connectionName"]!);
        Assert.Equal("ter-1", (string)card["tokenExchangeResource"]!["id"]!);
        Assert.Equal(TestTenant.BotAppIdUri, (string)card["tokenExchangeResource"]!["uri"]!); // no trailing slash
        Assert.Equal("aad-provider", (string)card["tokenExchangeResource"]!["providerId"]!);
        Assert.Equal("signin", (string)card["buttons"]![0]!["type"]!);
        Assert.Equal(FakeTokenService.SignInLink, (string)card["buttons"]![0]!["value"]!);
    }

    [Fact]
    public async Task Uses_configured_app_id_uri_when_the_connection_has_no_token_exchange_url()
    {
        _tokenService.Resource = new() { SignInLink = FakeTokenService.SignInLink };

        var body = await Run(Turns.Click());

        Assert.Equal(TestTenant.BotAppIdUri, (string)body["value"]!["tokenExchangeResource"]!["uri"]!);
    }

    [Fact]
    public async Task Second_invoke_with_sso_token_validates_exchanges_and_runs_the_handler()
    {
        await Run(Turns.Click(authentication: Turns.Sso("sso-token")));

        Assert.Equal(("sso-token", TestTenant.UserOid), Assert.Single(_validator.Calls));
        Assert.Equal("sso-token", Assert.Single(_strategy.ExchangedTokens));
        var (_, token) = Assert.Single(_handled);
        Assert.Equal(new UserToken("downstream-token", UserTokenSource.SsoThenObo, FakeValidator.Claims), token);
    }

    [Fact]
    public async Task Invalid_sso_token_replies_412_so_Teams_falls_back_to_sign_in()
    {
        _validator.RejectWith = "bad audience";

        var body = await Run(Turns.Click(authentication: Turns.Sso("forged")));

        Assert.Empty(_strategy.ExchangedTokens);
        Assert.Empty(_handled);
        Assert.Equal(412, (int)body["statusCode"]!);
        Assert.Equal("application/vnd.microsoft.error.preconditionFailed", (string)body["type"]!);
    }

    [Fact]
    public async Task Consent_or_mfa_needed_replies_loginRequest_without_sso_info()
    {
        _strategy.Exchange = _ => throw new InteractionRequiredException("AADSTS65001: consent required");

        var body = await Run(Turns.Click(authentication: Turns.Sso("sso-token")));

        Assert.Empty(_handled);
        Assert.Equal(401, (int)body["statusCode"]!);
        Assert.Equal("application/vnd.microsoft.activity.loginRequest", (string)body["type"]!);
        Assert.Null(body["value"]!["tokenExchangeResource"]);
    }

    [Fact]
    public async Task Non_interactive_exchange_errors_propagate()
    {
        var boom = new InvalidOperationException("AADSTS7000215: Invalid client secret provided");
        _strategy.Exchange = _ => throw boom;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Turns.Click(authentication: Turns.Sso("sso-token"))));
        Assert.Same(boom, thrown);
    }

    [Fact]
    public async Task After_fallback_sign_in_redeems_the_magic_code_from_state()
    {
        _tokenService.TokensByMagicCode["123456"] = "token-from-sign-in";

        await Run(Turns.Click(state: "123456"));

        Assert.Equal("123456", Assert.Single(_tokenService.GetUserTokenCodes));
        Assert.Equal(new UserToken("token-from-sign-in", UserTokenSource.SignInFallback), Assert.Single(_handled).Token);
    }

    [Fact]
    public async Task Unredeemable_magic_code_asks_the_user_to_sign_in_again()
    {
        var body = await Run(Turns.Click(state: "wrong"));

        Assert.Empty(_handled);
        Assert.Equal(401, (int)body["statusCode"]!);
        Assert.Null(body["value"]!["tokenExchangeResource"]);
    }

    [Fact]
    public async Task Cached_token_is_used_without_any_sso_round_trip()
    {
        _strategy.Cached = "cached-token";

        await Run(Turns.Click());

        Assert.Equal(new UserToken("cached-token", UserTokenSource.TokenServiceCache), Assert.Single(_handled).Token);
        Assert.Equal(0, _tokenService.SignInResourceCalls);
    }
}
