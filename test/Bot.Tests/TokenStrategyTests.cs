using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using TeamsSsoBot.Sso;

namespace Bot.Tests;

public sealed class OboTokenStrategyTests
{
    private static readonly string[] Scopes = ["api://downstream/access_as_user"];

    private sealed class FakeAcquirer(Func<string, IEnumerable<string>, string> acquire) : IOboTokenAcquirer
    {
        public Task<string> AcquireTokenOnBehalfOfAsync(string assertion, IEnumerable<string> scopes, CancellationToken cancellationToken) =>
            Task.FromResult(acquire(assertion, scopes));
    }

    private static OboTokenStrategy Strategy(Func<string, IEnumerable<string>, string> acquire) =>
        new(new FakeAcquirer(acquire), Options.Create(new DownstreamApiOptions { Scopes = Scopes }));

    private static Task<(string, UserTokenSource)> Exchange(OboTokenStrategy strategy) =>
        strategy.ExchangeSsoTokenAsync(Turns.Click(), "sso-token", TestContext.Current.CancellationToken);

    [Fact]
    public async Task Sends_the_sso_token_as_the_obo_assertion_for_the_downstream_scopes()
    {
        (string Assertion, string[] Scopes)? call = null;
        var strategy = Strategy((assertion, scopes) =>
        {
            call = (assertion, scopes.ToArray());
            return "downstream";
        });

        Assert.Equal(("downstream", UserTokenSource.SsoThenObo), await Exchange(strategy));
        Assert.Equal("sso-token", call!.Value.Assertion);
        Assert.Equal(Scopes, call.Value.Scopes);
    }

    [Fact]
    public async Task Is_stateless_and_never_returns_a_cached_token() =>
        Assert.Null(await Strategy((_, _) => "x").GetCachedTokenAsync(Turns.Click(), TestContext.Current.CancellationToken));

    [Fact]
    public async Task Maps_MsalUiRequiredException_to_interaction_required()
    {
        var strategy = Strategy((_, _) => throw new MsalUiRequiredException("invalid_grant", "AADSTS50076: MFA required"));

        var ex = await Assert.ThrowsAsync<InteractionRequiredException>(() => Exchange(strategy));
        Assert.Contains("AADSTS50076", ex.Message);
    }

    [Fact]
    public async Task Treats_AADSTS65001_service_error_as_interaction_required()
    {
        var strategy = Strategy((_, _) => throw new MsalServiceException("invalid_grant", "AADSTS65001: The user or administrator has not consented to use the application"));

        await Assert.ThrowsAsync<InteractionRequiredException>(() => Exchange(strategy));
    }

    [Fact]
    public async Task Rethrows_configuration_errors_such_as_a_bad_client_secret()
    {
        var strategy = Strategy((_, _) => throw new MsalServiceException("invalid_client", "AADSTS7000215: Invalid client secret provided."));

        await Assert.ThrowsAsync<MsalServiceException>(() => Exchange(strategy));
    }
}

public sealed class TokenServiceTokenStrategyTests
{
    [Fact]
    public async Task Returns_the_token_stored_in_the_token_store()
    {
        var strategy = new TokenServiceTokenStrategy(new FakeTokenService { StoredToken = "stored" });
        Assert.Equal("stored", await strategy.GetCachedTokenAsync(Turns.Click(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Exchanges_the_sso_token_through_the_token_service()
    {
        var tokenService = new FakeTokenService { ExchangeResult = "exchanged" };
        var result = await new TokenServiceTokenStrategy(tokenService).ExchangeSsoTokenAsync(Turns.Click(), "sso", TestContext.Current.CancellationToken);

        Assert.Equal(("exchanged", UserTokenSource.SsoThenTokenService), result);
        Assert.Equal("sso", Assert.Single(tokenService.ExchangedTokens));
    }

    [Fact]
    public async Task Signals_interaction_required_when_the_token_service_cannot_exchange() =>
        await Assert.ThrowsAsync<InteractionRequiredException>(() =>
            new TokenServiceTokenStrategy(new FakeTokenService()).ExchangeSsoTokenAsync(Turns.Click(), "sso", TestContext.Current.CancellationToken));
}
