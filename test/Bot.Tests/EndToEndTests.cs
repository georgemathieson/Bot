using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TeamsSsoBot;
using TeamsSsoBot.Sso;
using TestSupport;

namespace Bot.Tests;

/// <summary>
/// End to end: the real bot (Agents SDK pipeline on POST /api/messages) + the real downstream API
/// (Microsoft.Identity.Web), with only the parts that live in Microsoft's cloud simulated:
/// <list type="bullet">
/// <item>Entra ID: a local signing key, and fake managed identity and token endpoints under the real MSAL code
/// (managed identity token, then OBO with that token as the SSO app's credential).</item>
/// <item>Bot Framework token service: a stub returning a sign-in link + tokenExchangeResource.</item>
/// <item>Teams client: this test, which re-sends the invoke with an SSO token after the 401.</item>
/// </list>
/// </summary>
public sealed class EndToEndTests : IDisposable
{
    private readonly TestTenant _tenant = new();
    private readonly DownstreamApiFactory _api;
    private readonly BotFactory _bot;
    private readonly HttpClient _client;
    private readonly FakeEntraHttp _entra;

    public EndToEndTests()
    {
        _api = new DownstreamApiFactory(_tenant);
        _entra = new FakeEntraHttp(_tenant);
        _bot = new BotFactory(_tenant, _api, _entra);
        _client = _bot.CreateClient();
    }

    private async Task<JsonNode> Invoke(object? authentication = null, string verb = Cards.CallApiVerb, string? aadObjectId = TestTenant.UserOid)
    {
        var activity = new
        {
            type = "invoke",
            name = "adaptiveCard/action",
            id = "activity-1",
            channelId = "msteams",
            serviceUrl = "https://smba.trafficmanager.net/teams/",
            from = new { id = "29:user-1", name = "Adele Vance", aadObjectId },
            recipient = new { id = "28:" + TestTenant.BotClientId, name = "bot" },
            conversation = new { id = "a:conversation-1", conversationType = "personal", tenantId = TestTenant.TenantId },
            channelData = new { tenant = new { id = TestTenant.TenantId } },
            value = new
            {
                action = new { type = "Action.Execute", verb, data = new { action = verb } },
                trigger = "manual",
                authentication,
            },
        };

        var response = await _client.PostAsJsonAsync("/api/messages", activity, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // invoke responses travel in the HTTP body
        return (await response.Content.ReadFromJsonAsync<JsonNode>(TestContext.Current.CancellationToken))!;
    }

    private static object Sso(string token) => new { id = "ter-1", connectionName = "sso", token };

    [Fact]
    public async Task First_click_asks_Teams_for_an_sso_token()
    {
        var body = await Invoke();

        Assert.Equal(401, (int)body["statusCode"]!);
        Assert.Equal("application/vnd.microsoft.activity.loginRequest", (string)body["type"]!);
        Assert.Equal(TestTenant.BotAppIdUri, (string)body["value"]!["tokenExchangeResource"]!["uri"]!);
        Assert.Equal("signin", (string)body["value"]!["buttons"]![0]!["type"]!);
    }

    [Fact]
    public async Task Retry_with_sso_token_runs_obo_and_the_api_sees_the_real_user()
    {
        var ssoToken = _tenant.SsoToken();

        var body = await Invoke(Sso(ssoToken));

        // The OBO request: by the SSO app, with the managed identity token as its credential.
        var obo = Assert.Single(_entra.TokenRequests);
        Assert.Equal(ssoToken, obo["assertion"]);
        Assert.Equal(TestTenant.SsoAppClientId, obo["client_id"]);
        Assert.Equal(FakeEntraHttp.ManagedIdentityToken, obo["client_assertion"]);
        Assert.Contains(TestTenant.ApiAppIdUri + "/access_as_user", obo["scope"].Split(' '));
        // MSAL caches the managed identity token for the whole process, so an earlier test may have fetched it.
        Assert.All(_entra.ManagedIdentityRequests, mi => Assert.Equal(TestTenant.BotClientId, FakeEntraHttp.Query(mi, "client_id")));

        Assert.Equal(200, (int)body["statusCode"]!);
        Assert.Equal("application/vnd.microsoft.card.adaptive", (string)body["type"]!);
        var text = body["value"]!.ToJsonString();
        Assert.Contains("Hello Adele Vance, the downstream API accepted your token.", text);
        Assert.Contains(TestTenant.UserOid, text);
        Assert.Contains("Teams SSO, then the bot ran on-behalf-of with MSAL", text);

        // The chain: the API saw a token for itself requested by the bot; the bot saw a token for itself requested by Teams.
        var facts = body["value"]!["body"]!.AsArray()
            .Where(b => (string?)b!["type"] == "FactSet")
            .SelectMany(b => b!["facts"]!.AsArray())
            .Select(f => ((string)f!["title"]!, (string)f["value"]!))
            .ToList();
        Assert.Contains(("aud", TestTenant.ApiClientId), facts);
        Assert.Contains(("azp/appid (caller)", TestTenant.SsoAppClientId), facts);
        Assert.Contains(("aud", TestTenant.SsoAppClientId), facts);
        Assert.Contains(("azp/appid (caller)", TestTenant.TeamsDesktopClientId + " (Teams desktop/mobile)"), facts);
    }

    [Fact]
    public async Task Accepts_a_v1_sso_token_end_to_end()
    {
        var body = await Invoke(Sso(_tenant.SsoToken(TestTenant.AsV1(TestTenant.BotAppIdUri))));

        Assert.Equal(200, (int)body["statusCode"]!);
        Assert.Contains("the downstream API accepted your token", body["value"]!.ToJsonString());
    }

    [Fact]
    public async Task Rejects_an_sso_token_belonging_to_another_user()
    {
        var body = await Invoke(Sso(_tenant.SsoToken(c => c["oid"] = TestTenant.OtherUserOid)));

        Assert.Equal(412, (int)body["statusCode"]!);
        Assert.Empty(_entra.TokenRequests);
    }

    [Fact]
    public async Task Falls_back_to_the_sign_in_button_when_Entra_says_consent_is_missing()
    {
        _entra.NextError = ("invalid_grant", "AADSTS65001: The user or administrator has not consented to use the application");

        var body = await Invoke(Sso(_tenant.SsoToken()));

        Assert.Equal(401, (int)body["statusCode"]!);
        Assert.Equal("application/vnd.microsoft.activity.loginRequest", (string)body["type"]!);
        Assert.Null(body["value"]!["tokenExchangeResource"]);
    }

    [Fact]
    public async Task Shows_an_error_card_with_the_Entra_error_for_configuration_problems()
    {
        // What Entra says when the SSO app has no federated credential for the managed identity (or it isn't replicated yet).
        _entra.NextError = ("invalid_client", "AADSTS70021: No matching federated identity record found for presented assertion.");

        var body = await Invoke(Sso(_tenant.SsoToken()));

        Assert.Equal(200, (int)body["statusCode"]!);
        Assert.Equal("application/vnd.microsoft.card.adaptive", (string)body["type"]!);
        var text = body["value"]!.ToJsonString();
        Assert.Contains("invalid_client", text);
        Assert.Contains("AADSTS70021: No matching federated identity record found for presented assertion.", text);
    }

    [Fact]
    public async Task Unknown_verbs_get_400_instead_of_a_sign_in()
    {
        var body = await Invoke(verb: "deleteEverything");

        Assert.Equal(400, (int)body["statusCode"]!);
        Assert.Contains("deleteEverything", (string)body["value"]!["message"]!);
    }

    public void Dispose()
    {
        _client.Dispose();
        _bot.Dispose();
        _api.Dispose();
        _entra.Dispose();
        _tenant.Dispose();
    }

    /// <summary>The bot as deployed to Azure: a user-assigned managed identity bot and a separate SSO app registration.</summary>
    private sealed class BotFactory(TestTenant tenant, DownstreamApiFactory api, FakeEntraHttp entra) : WebApplicationFactory<CardActionSso>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            // The bot's identity: a user-assigned managed identity (no outbound Bot Framework call happens in these tests).
            builder.UseSetting("Connections:ServiceConnection:Settings:AuthType", "UserManagedIdentity");
            builder.UseSetting("Connections:ServiceConnection:Settings:ClientId", TestTenant.BotClientId);
            builder.UseSetting("Connections:ServiceConnection:Settings:TenantId", TestTenant.TenantId);
            builder.UseSetting("SsoApp:ClientId", TestTenant.SsoAppClientId); // its credential defaults to the bot's managed identity
            // No Bot Connector JWT in a local test.
            builder.UseSetting("TokenValidation:Enabled", "false");
            builder.UseSetting("Sso:BotAppIdUri", "");
            builder.UseSetting("Sso:TokenStrategy", "Obo");
            builder.UseSetting("DownstreamApi:Url", "http://localhost/api/me");
            builder.UseSetting("DownstreamApi:Scopes:0", TestTenant.ApiAppIdUri + "/access_as_user");

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ITokenServiceClient>();
                services.AddSingleton<ITokenServiceClient>(new FakeTokenService());
                // The real MSAL acquirer; only its HTTP calls (managed identity endpoint, Entra) are simulated.
                services.RemoveAll<IOboTokenAcquirer>();
                services.AddSingleton<IOboTokenAcquirer>(sp => new MsalOboTokenAcquirer(sp.GetRequiredService<IOptions<SsoAppOptions>>(), entra));
                services.RemoveAll<ISsoTokenValidator>();
                services.AddSingleton<ISsoTokenValidator>(sp => new SsoTokenValidator(
                    sp.GetRequiredService<IOptions<SsoAppOptions>>(),
                    sp.GetRequiredService<IOptions<SsoOptions>>(),
                    tenant.ConfigurationManager));
                // The bot reaches the in-process downstream API.
                services.AddHttpClient(DownstreamApiClient.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => api.Server.CreateHandler());
            });
        }
    }
}
