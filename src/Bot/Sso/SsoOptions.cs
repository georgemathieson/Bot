namespace TeamsSsoBot.Sso;

public enum TokenStrategy
{
    /// <summary>The bot performs the on-behalf-of exchange itself with MSAL.</summary>
    Obo,

    /// <summary>The Bot Framework token service performs the exchange using the Azure Bot's OAuth connection.</summary>
    TokenService,
}

public sealed class SsoOptions
{
    public const string SectionName = "Sso";

    /// <summary>Name of the OAuth connection on the Azure Bot resource.</summary>
    public string OAuthConnectionName { get; set; } = "sso";

    /// <summary>
    /// Application ID URI the bot exposes for Teams SSO. Defaults to <c>api://botid-{SsoApp ClientId}</c>. Must equal:
    /// the SSO app registration's Application ID URI, manifest <c>webApplicationInfo.resource</c>, and the OAuth
    /// connection's Token Exchange URL.
    /// </summary>
    public string? BotAppIdUri { get; set; }

    /// <summary>Delegated scope the bot exposes for Teams SSO.</summary>
    public string BotSsoScope { get; set; } = "access_as_user";

    public TokenStrategy TokenStrategy { get; set; } = TokenStrategy.Obo;

    /// <summary>POC diagnostics: show decoded (never raw) token claims and error details on cards.</summary>
    public bool ShowDiagnostics { get; set; } = true;
}

/// <summary>
/// The Entra app registration that Teams SSO targets (manifest <c>webApplicationInfo.id</c>) and that runs the
/// on-behalf-of exchange. The SSO token's audience is this app, so the OBO request must come from this app.
/// <para>
/// This is not a second identity of the bot. The bot's identity is the user-assigned managed identity in
/// <c>AzureAd</c>. A managed identity can't expose an API scope, so Teams SSO needs an app registration; this app
/// registration trusts the bot's managed identity through a federated identity credential, and by default the
/// bot's managed identity is its credential (no secret, nothing configured twice).
/// </para>
/// <para>
/// Bound by <see cref="Bind"/>: from <c>SsoApp</c>, or, when <c>SsoApp:ClientId</c> is empty, from the bot's own
/// <c>AzureAd</c> app registration (the single-app setup used to run the bot on your computer with a client secret).
/// </para>
/// </summary>
public sealed class SsoAppOptions
{
    public const string SectionName = "SsoApp";
    public const string BotSectionName = "AzureAd";

    public string Instance { get; set; } = "https://login.microsoftonline.com/";
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public List<ClientCredentialOptions> ClientCredentials { get; set; } = [];

    /// <summary>The section that <see cref="ClientId"/> and <see cref="ClientCredentials"/> came from, for error messages.</summary>
    public string Source { get; set; } = SectionName;

    public static void Bind(IConfiguration configuration, SsoAppOptions options)
    {
        var ssoApp = configuration.GetSection(SectionName);
        var bot = configuration.GetSection(BotSectionName);
        var usesSsoApp = !string.IsNullOrWhiteSpace(ssoApp["ClientId"]);
        var app = usesSsoApp ? ssoApp : bot;

        options.Source = app.Key;
        options.ClientId = app["ClientId"] ?? string.Empty;
        // ClientId and ClientCredentials always come from the same section: another app's credential can't work.
        options.ClientCredentials = app.GetSection("ClientCredentials").Get<List<ClientCredentialOptions>>() ?? [];

        // The bot has one identity, in AzureAd. When it is a user-assigned managed identity (a ClientId and no
        // ClientCredentials, the same rule the Teams SDK applies), that identity is also the SSO app's credential,
        // so it is configured once. SsoApp:ClientCredentials is only needed to override this.
        var botManagedIdentity = IsManagedIdentity(bot) ? bot["ClientId"] : null;
        if (usesSsoApp && botManagedIdentity is not null)
        {
            if (options.ClientCredentials.Count == 0)
            {
                options.ClientCredentials = [new() { SourceType = ClientCredentialOptions.SignedAssertionFromManagedIdentity }];
            }
            foreach (var credential in options.ClientCredentials.Where(c =>
                string.Equals(c.SourceType, ClientCredentialOptions.SignedAssertionFromManagedIdentity, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(c.ManagedIdentityClientId)))
            {
                credential.ManagedIdentityClientId = botManagedIdentity;
            }
        }
        // The SSO app, the managed identity and the bot are in one tenant (federated credentials require it).
        options.TenantId = FirstValue(ssoApp["TenantId"], bot["TenantId"]) ?? string.Empty;
        options.Instance = FirstValue(ssoApp["Instance"], bot["Instance"]) ?? options.Instance;
    }

    private static bool IsManagedIdentity(IConfigurationSection section) =>
        !string.IsNullOrWhiteSpace(section["ClientId"]) && !section.GetSection("ClientCredentials").GetChildren().Any();

    private static string? FirstValue(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

/// <summary>One entry of <c>ClientCredentials</c>. Same keys as Microsoft.Identity.Web's credential descriptions.</summary>
public sealed class ClientCredentialOptions
{
    public const string SignedAssertionFromManagedIdentity = "SignedAssertionFromManagedIdentity";
    public const string ClientSecretSourceType = "ClientSecret";

    /// <summary><c>SignedAssertionFromManagedIdentity</c> (in Azure) or <c>ClientSecret</c> (on your computer).</summary>
    public string? SourceType { get; set; }

    /// <summary>For <c>SignedAssertionFromManagedIdentity</c>: client ID of the user-assigned managed identity.</summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>
    /// For <c>SignedAssertionFromManagedIdentity</c>: audience of the managed identity token. Must match the
    /// federated identity credential. Defaults to <c>api://AzureADTokenExchange</c> (global Azure).
    /// </summary>
    public string? TokenExchangeUrl { get; set; }

    /// <summary>For <c>ClientSecret</c>. Keep it in user-secrets, never in appsettings.json.</summary>
    public string? ClientSecret { get; set; }
}

public sealed class DownstreamApiOptions
{
    public const string SectionName = "DownstreamApi";

    public Uri? Url { get; set; }

    /// <summary>Delegated scopes for the downstream API, e.g. <c>api://{api-client-id}/access_as_user</c>.</summary>
    public string[] Scopes { get; set; } = [];
}
