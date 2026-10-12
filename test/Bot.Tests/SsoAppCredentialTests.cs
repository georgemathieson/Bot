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
        ["Connections:ServiceConnection:Settings:AuthType"] = "UserManagedIdentity",
        ["Connections:ServiceConnection:Settings:ClientId"] = TestTenant.BotClientId, // the bot's identity: the managed identity
        ["Connections:ServiceConnection:Settings:TenantId"] = TestTenant.TenantId,
    };

    [Fact]
    public void The_bot_managed_identity_is_the_sso_app_credential_without_configuring_it_again()
    {
        // The whole bot configuration in Azure: the bot's identity once, and the SSO app registration's ID.
        var options = Bind(new(ManagedIdentityBot) { ["SsoApp:ClientId"] = TestTenant.SsoAppClientId });

        Assert.Equal("SsoApp", options.Source);
        Assert.Equal(TestTenant.SsoAppClientId, options.ClientId);
        Assert.Equal(TestTenant.TenantId, options.TenantId); // from the service connection: one tenant
        Assert.Equal("https://login.microsoftonline.com/", options.Instance);
        var credential = Assert.Single(options.ClientCredentials);
        Assert.Equal("SignedAssertionFromManagedIdentity", credential.SourceType);
        Assert.Equal(TestTenant.BotClientId, credential.ManagedIdentityClientId);
    }

    [Fact]
    public void A_managed_identity_credential_without_a_client_id_uses_the_bot_managed_identity()
    {
        var options = Bind(new(ManagedIdentityBot)
        {
            ["SsoApp:ClientId"] = TestTenant.SsoAppClientId,
            ["SsoApp:ClientCredentials:0:SourceType"] = "SignedAssertionFromManagedIdentity",
            ["SsoApp:ClientCredentials:0:TokenExchangeUrl"] = "api://AzureADTokenExchangeUSGov", // e.g. a sovereign cloud
        });

        var credential = Assert.Single(options.ClientCredentials);
        Assert.Equal(TestTenant.BotClientId, credential.ManagedIdentityClientId);
        Assert.Equal("api://AzureADTokenExchangeUSGov", credential.TokenExchangeUrl);
    }

    [Fact]
    public void An_explicit_SsoApp_credential_overrides_the_default()
    {
        const string otherManagedIdentity = "77777777-7777-7777-7777-777777777777";
        var options = Bind(new(ManagedIdentityBot)
        {
            ["SsoApp:ClientId"] = TestTenant.SsoAppClientId,
            ["SsoApp:ClientCredentials:0:SourceType"] = "SignedAssertionFromManagedIdentity",
            ["SsoApp:ClientCredentials:0:ManagedIdentityClientId"] = otherManagedIdentity,
        });

        Assert.Equal(otherManagedIdentity, Assert.Single(options.ClientCredentials).ManagedIdentityClientId);
    }

    [Fact]
    public void Without_SsoApp_the_bot_app_registration_and_its_secret_are_used()
    {
        var options = Bind(new()
        {
            ["Connections:ServiceConnection:Settings:AuthType"] = "ClientSecret",
            ["Connections:ServiceConnection:Settings:ClientId"] = TestTenant.SsoAppClientId,
            ["Connections:ServiceConnection:Settings:ClientSecret"] = "s3cret",
            ["Connections:ServiceConnection:Settings:AuthorityEndpoint"] = "https://login.microsoftonline.com/" + TestTenant.TenantId,
            ["SsoApp:ClientId"] = "", // as shipped in appsettings.json
        });

        Assert.Equal(BotIdentity.SectionName, options.Source);
        Assert.Equal(TestTenant.TenantId, options.TenantId); // from AuthorityEndpoint
        Assert.Equal(TestTenant.SsoAppClientId, options.ClientId);
        Assert.Equal("s3cret", Assert.Single(options.ClientCredentials).ClientSecret);
    }

    [Fact]
    public void SsoApp_never_borrows_the_credential_of_the_bot_app_registration()
    {
        var options = Bind(new()
        {
            ["Connections:ServiceConnection:Settings:AuthType"] = "ClientSecret",
            ["Connections:ServiceConnection:Settings:ClientId"] = TestTenant.BotClientId,
            ["Connections:ServiceConnection:Settings:ClientSecret"] = "bot-secret",
            ["Connections:ServiceConnection:Settings:TenantId"] = TestTenant.TenantId,
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
        Assert.Contains("set SsoApp:ClientId to the SSO app registration", ex.Message);
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

    [Fact]
    public void Inbound_token_validation_defaults_to_the_bot_identity()
    {
        var options = BotIdentity.TokenValidation(new ConfigurationBuilder().AddInMemoryCollection(ManagedIdentityBot).Build());

        Assert.Equal([TestTenant.BotClientId], options.Audiences); // the Bot Connector's token is for the bot
        Assert.Equal(TestTenant.TenantId, options.TenantId);
    }

    public void Dispose()
    {
        _entra.Dispose();
        _tenant.Dispose();
    }
}
