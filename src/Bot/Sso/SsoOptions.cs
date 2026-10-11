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
    /// Application ID URI the bot exposes for Teams SSO. Defaults to <c>api://botid-{ClientId}</c>. Must equal:
    /// the app registration's Application ID URI, manifest <c>webApplicationInfo.resource</c>, and the OAuth
    /// connection's Token Exchange URL.
    /// </summary>
    public string? BotAppIdUri { get; set; }

    /// <summary>Delegated scope the bot exposes for Teams SSO.</summary>
    public string BotSsoScope { get; set; } = "access_as_user";

    public TokenStrategy TokenStrategy { get; set; } = TokenStrategy.Obo;

    /// <summary>POC diagnostics: show decoded (never raw) token claims and error details on cards.</summary>
    public bool ShowDiagnostics { get; set; } = true;
}

/// <summary>The bot's Entra app registration, read from the same <c>AzureAd</c> section the Teams SDK uses.</summary>
public sealed class BotIdentityOptions
{
    public string Instance { get; set; } = "https://login.microsoftonline.com/";
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public List<ClientCredential> ClientCredentials { get; set; } = [];

    public string? ClientSecret =>
        ClientCredentials.FirstOrDefault(c => string.Equals(c.SourceType, "ClientSecret", StringComparison.OrdinalIgnoreCase))?.ClientSecret;

    public sealed class ClientCredential
    {
        public string? SourceType { get; set; }
        public string? ClientSecret { get; set; }
    }
}

public sealed class DownstreamApiOptions
{
    public const string SectionName = "DownstreamApi";

    public Uri? Url { get; set; }

    /// <summary>Delegated scopes for the downstream API, e.g. <c>api://{api-client-id}/access_as_user</c>.</summary>
    public string[] Scopes { get; set; } = [];
}
