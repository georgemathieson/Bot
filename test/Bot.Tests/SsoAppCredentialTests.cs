using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TeamsSsoBot.Sso;
using TestSupport;

namespace Bot.Tests;

/// <summary>The OBO request is made by the SSO app registration, with a managed identity token as its credential.</summary>
public sealed class SsoAppCredentialTests : IDisposable
{
    private static readonly string[] Scopes = [TestTenant.ApiAppIdUri + "/access_as_user"];
    private readonly TestTenant _tenant = new();
    private readonly FakeEntraHttp _entra;
    // MSAL caches managed identity tokens for the whole process, per managed identity: a new one per test keeps tests independent.
    private readonly string _managedIdentityClientId = Guid.NewGuid().ToString();

    public SsoAppCredentialTests() => _entra = new FakeEntraHttp(_tenant);

    private SsoAppOptions ManagedIdentityApp() => new()
    {
        TenantId = TestTenant.TenantId,
        ClientId = TestTenant.SsoAppClientId,
        ClientCredentials = [new() { SourceType = "SignedAssertionFromManagedIdentity", ManagedIdentityClientId = _managedIdentityClientId }],
    };

    private MsalOboTokenAcquirer Acquirer(SsoAppOptions app) => new(Options.Create(app), _entra);

    [Fact]
    public async Task Obo_request_uses_a_managed_identity_token_as_the_client_assertion()
    {
        var ssoToken = _tenant.SsoToken();

        var downstream = await Acquirer(ManagedIdentityApp()).AcquireTokenOnBehalfOfAsync(ssoToken, Scopes, TestContext.Current.CancellationToken);

        // 1. The managed identity token: for the federated credential audience, from the user-assigned identity.
        var mi = Assert.Single(_entra.ManagedIdentityRequests);
        Assert.Equal("api://AzureADTokenExchange", FakeEntraHttp.Query(mi, "resource"));
        Assert.Equal(_managedIdentityClientId, FakeEntraHttp.Query(mi, "client_id"));

        // 2. The OBO request: made by the SSO app, the managed identity token as its credential, no secret.
        var obo = Assert.Single(_entra.TokenRequests);
        Assert.Equal(TestTenant.SsoAppClientId, obo["client_id"]);
        Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", obo["client_assertion_type"]);
        Assert.Equal(FakeEntraHttp.ManagedIdentityToken, obo["client_assertion"]);
        Assert.False(obo.ContainsKey("client_secret"));
        Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", obo["grant_type"]);
        Assert.Equal("on_behalf_of", obo["requested_token_use"]);
        Assert.Equal(ssoToken, obo["assertion"]);
        Assert.Contains(Scopes[0], obo["scope"].Split(' '));

        Assert.Empty(_entra.UnexpectedRequests);
        Assert.Equal(TestTenant.ApiClientId, new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(downstream).Audiences.Single());
    }

    [Fact]
    public async Task The_managed_identity_token_is_reused_for_later_obo_requests()
    {
        var acquirer = Acquirer(ManagedIdentityApp());

        await acquirer.AcquireTokenOnBehalfOfAsync(_tenant.SsoToken(), Scopes, TestContext.Current.CancellationToken);
        await acquirer.AcquireTokenOnBehalfOfAsync(_tenant.SsoToken(c => c["oid"] = TestTenant.OtherUserOid), Scopes, TestContext.Current.CancellationToken);

        Assert.Equal(2, _entra.TokenRequests.Count); // two users, two OBO requests
        Assert.Single(_entra.ManagedIdentityRequests); // one managed identity token
    }

    [Fact]
    public async Task A_custom_token_exchange_url_is_used_as_the_managed_identity_audience()
    {
        var app = ManagedIdentityApp();
        app.ClientCredentials[0].TokenExchangeUrl = "api://AzureADTokenExchangeUSGov";

        await Acquirer(app).AcquireTokenOnBehalfOfAsync(_tenant.SsoToken(), Scopes, TestContext.Current.CancellationToken);

        Assert.Equal("api://AzureADTokenExchangeUSGov", FakeEntraHttp.Query(Assert.Single(_entra.ManagedIdentityRequests), "resource"));
    }

    [Fact]
    public async Task On_your_computer_the_obo_request_uses_the_client_secret()
    {
        var app = new SsoAppOptions
        {
            TenantId = TestTenant.TenantId,
            ClientId = TestTenant.SsoAppClientId,
            ClientCredentials = [new() { SourceType = "ClientSecret", ClientSecret = FakeEntraHttp.ClientSecret }],
        };

        await Acquirer(app).AcquireTokenOnBehalfOfAsync(_tenant.SsoToken(), Scopes, TestContext.Current.CancellationToken);

        var obo = Assert.Single(_entra.TokenRequests);
        Assert.Equal(FakeEntraHttp.ClientSecret, obo["client_secret"]);
        Assert.False(obo.ContainsKey("client_assertion"));
        Assert.Empty(_entra.ManagedIdentityRequests);
    }

    [Fact]
    public void Managed_identity_credential_without_a_client_id_is_rejected_at_startup()
    {
        var app = ManagedIdentityApp();
        app.ClientCredentials[0].ManagedIdentityClientId = "";

        var ex = Assert.Throws<InvalidOperationException>(() => Acquirer(app));
        Assert.Contains("SsoApp:ClientCredentials:0:ManagedIdentityClientId", ex.Message);
    }

    [Fact]
    public void Unsupported_credential_types_are_rejected_at_startup()
    {
        var app = ManagedIdentityApp();
        app.ClientCredentials[0].SourceType = "KeyVault";

        var ex = Assert.Throws<InvalidOperationException>(() => Acquirer(app));
        Assert.Contains("'KeyVault' is not supported", ex.Message);
    }

    // ---- Configuration: where the SSO app's settings come from ------------------------------------------------

    private static SsoAppOptions Bind(Dictionary<string, string?> settings)
    {
        var options = new SsoAppOptions();
        SsoAppOptions.Bind(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(), options);
        return options;
    }

    private static readonly Dictionary<string, string?> ManagedIdentityBot = new()
    {
        ["AzureAd:TenantId"] = TestTenant.TenantId,
        ["AzureAd:ClientId"] = TestTenant.BotClientId, // the managed identity; no ClientCredentials
    };

    [Fact]
    public void With_a_managed_identity_bot_the_sso_app_comes_from_the_SsoApp_section()
    {
        var options = Bind(new(ManagedIdentityBot)
        {
            ["SsoApp:ClientId"] = TestTenant.SsoAppClientId,
            ["SsoApp:ClientCredentials:0:SourceType"] = "SignedAssertionFromManagedIdentity",
            ["SsoApp:ClientCredentials:0:ManagedIdentityClientId"] = TestTenant.BotClientId,
        });

        Assert.Equal("SsoApp", options.Source);
        Assert.Equal(TestTenant.SsoAppClientId, options.ClientId);
        Assert.Equal(TestTenant.TenantId, options.TenantId); // from AzureAd: one tenant
        Assert.Equal("https://login.microsoftonline.com/", options.Instance);
        var credential = Assert.Single(options.ClientCredentials);
        Assert.Equal(TestTenant.BotClientId, credential.ManagedIdentityClientId);
    }

    [Fact]
    public void Without_SsoApp_the_bot_app_registration_and_its_secret_are_used()
    {
        var options = Bind(new()
        {
            ["AzureAd:TenantId"] = TestTenant.TenantId,
            ["AzureAd:ClientId"] = TestTenant.SsoAppClientId,
            ["AzureAd:ClientCredentials:0:SourceType"] = "ClientSecret",
            ["AzureAd:ClientCredentials:0:ClientSecret"] = "s3cret",
            // appsettings.json ships an empty SsoApp entry; it must not leak into the single-app setup.
            ["SsoApp:ClientId"] = "",
            ["SsoApp:ClientCredentials:0:SourceType"] = "SignedAssertionFromManagedIdentity",
            ["SsoApp:ClientCredentials:0:ManagedIdentityClientId"] = "",
        });

        Assert.Equal("AzureAd", options.Source);
        Assert.Equal(TestTenant.SsoAppClientId, options.ClientId);
        Assert.Equal("s3cret", Assert.Single(options.ClientCredentials).ClientSecret);
    }

    [Fact]
    public void SsoApp_never_borrows_the_credential_of_the_bot_app_registration()
    {
        var options = Bind(new()
        {
            ["AzureAd:TenantId"] = TestTenant.TenantId,
            ["AzureAd:ClientId"] = TestTenant.BotClientId,
            ["AzureAd:ClientCredentials:0:SourceType"] = "ClientSecret",
            ["AzureAd:ClientCredentials:0:ClientSecret"] = "bot-secret",
            ["SsoApp:ClientId"] = TestTenant.SsoAppClientId,
        });

        Assert.Empty(options.ClientCredentials);
        var ex = Assert.Throws<InvalidOperationException>(() => new MsalOboTokenAcquirer(Options.Create(options)));
        Assert.Contains("SsoApp:ClientCredentials is empty", ex.Message);
    }

    [Fact]
    public void A_managed_identity_bot_without_SsoApp_fails_at_startup_with_a_hint()
    {
        var options = Bind(new(ManagedIdentityBot));

        var ex = Assert.Throws<InvalidOperationException>(() => new MsalOboTokenAcquirer(Options.Create(options)));
        Assert.Contains("set SsoApp:ClientId and SsoApp:ClientCredentials", ex.Message);
    }

    [Fact]
    public void The_sso_resource_defaults_to_the_sso_app_not_the_managed_identity()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(ManagedIdentityBot)
        {
            ["SsoApp:ClientId"] = TestTenant.SsoAppClientId,
        }).Build();
        using var provider = new ServiceCollection().AddLogging().AddCardActionSso(configuration).BuildServiceProvider();

        Assert.Equal(TestTenant.BotAppIdUri, provider.GetRequiredService<IOptions<SsoOptions>>().Value.BotAppIdUri);
        Assert.Equal("api://botid-" + TestTenant.SsoAppClientId, TestTenant.BotAppIdUri);
    }

    public void Dispose()
    {
        _entra.Dispose();
        _tenant.Dispose();
    }
}
