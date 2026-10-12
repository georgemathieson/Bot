namespace TeamsSsoBot.Sso;

/// <summary>
/// The bot's one identity: the Agents SDK service connection (<c>Connections:ServiceConnection:Settings</c>), which the
/// SDK uses for Bot Framework traffic. In Azure its <c>AuthType</c> is <c>UserManagedIdentity</c> and its
/// <c>ClientId</c> is the client ID of the user-assigned managed identity (Azure Bot type "User-Assigned Managed
/// Identity"). Everything else that needs the bot's identity reads it from here, so it is configured once.
/// </summary>
public sealed class BotIdentity
{
    public const string SectionName = "Connections:ServiceConnection:Settings";
    public const string UserManagedIdentity = "UserManagedIdentity";
    public const string ClientSecret = "ClientSecret";

    public string ClientId { get; init; } = string.Empty;

    /// <summary>The SDK's <c>AuthType</c>; the SDK treats a missing value as <c>ClientSecret</c>.</summary>
    public string AuthType { get; init; } = ClientSecret;

    public string? Secret { get; init; }

    public string? TenantId { get; init; }

    /// <summary>The login host, from <c>AuthorityEndpoint</c> (e.g. <c>https://login.microsoftonline.com/</c>), if set.</summary>
    public string? Instance { get; init; }

    public bool IsManagedIdentity =>
        !string.IsNullOrWhiteSpace(ClientId) && string.Equals(AuthType, UserManagedIdentity, StringComparison.OrdinalIgnoreCase);

    public bool HasClientSecret =>
        string.Equals(AuthType, ClientSecret, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(Secret);

    public static BotIdentity From(IConfiguration configuration)
    {
        var settings = configuration.GetSection(SectionName);
        Uri.TryCreate(settings["AuthorityEndpoint"], UriKind.Absolute, out var authority);
        // AuthorityEndpoint is "https://login.microsoftonline.com/{tenant}".
        var authorityTenant = authority?.Segments.ElementAtOrDefault(1)?.Trim('/');

        return new BotIdentity
        {
            ClientId = settings["ClientId"] ?? string.Empty,
            AuthType = string.IsNullOrWhiteSpace(settings["AuthType"]) ? ClientSecret : settings["AuthType"]!,
            Secret = settings["ClientSecret"],
            TenantId = FirstValue(settings["TenantId"], authorityTenant),
            Instance = authority is null ? null : authority.GetLeftPart(UriPartial.Authority) + "/",
        };
    }

    /// <summary>
    /// Inbound token validation for <c>/api/messages</c>. The Bot Connector's token has the bot's client ID as its
    /// audience, so <c>TokenValidation:Audiences</c> and <c>TokenValidation:TenantId</c> default to this identity.
    /// </summary>
    public static AspNetExtensions.TokenValidationOptions TokenValidation(IConfiguration configuration)
    {
        var options = configuration.GetSection("TokenValidation").Get<AspNetExtensions.TokenValidationOptions>()
            ?? new AspNetExtensions.TokenValidationOptions();
        var bot = From(configuration);
        if (options.Audiences is not { Count: > 0 } && !string.IsNullOrWhiteSpace(bot.ClientId))
        {
            options.Audiences = [bot.ClientId];
        }
        if (string.IsNullOrWhiteSpace(options.TenantId))
        {
            options.TenantId = bot.TenantId;
        }
        return options;
    }

    private static string? FirstValue(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
