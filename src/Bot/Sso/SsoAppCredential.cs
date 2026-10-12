using Microsoft.Identity.Client;
using Microsoft.Identity.Client.AppConfig;

namespace TeamsSsoBot.Sso;

/// <summary>Adds the SSO app registration's credential to the MSAL confidential client that runs OBO.</summary>
public static class SsoAppCredential
{
    /// <summary>Audience of the managed identity token for a federated identity credential in global Azure.</summary>
    public const string DefaultTokenExchangeUrl = "api://AzureADTokenExchange";

    /// <summary>
    /// <c>SignedAssertionFromManagedIdentity</c>: the bot gets a token for <c>api://AzureADTokenExchange</c> from its
    /// user-assigned managed identity and sends it as the client assertion. Entra accepts it because the SSO app has a
    /// federated identity credential that trusts this managed identity. No secret exists anywhere.
    /// <para><c>ClientSecret</c>: for running the bot on your computer, where there is no managed identity.</para>
    /// </summary>
    /// <param name="httpClientFactory">Optional HTTP client for MSAL (proxies, tests). Used for both token requests.</param>
    public static ConfidentialClientApplicationBuilder WithSsoAppCredential(
        this ConfidentialClientApplicationBuilder builder,
        SsoAppOptions app,
        IMsalHttpClientFactory? httpClientFactory = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(app);

        var credential = app.ClientCredentials.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.SourceType))
            ?? throw new InvalidOperationException(
                $"{app.Source}:ClientCredentials is empty, so the bot can't make the OBO request as {app.Source}:ClientId. " +
                (app.Source == SsoAppOptions.BotSectionName
                    ? "If the bot uses a user-assigned managed identity, set SsoApp:ClientId and SsoApp:ClientCredentials " +
                      "(SourceType SignedAssertionFromManagedIdentity) for the separate SSO app registration."
                    : "Add a SignedAssertionFromManagedIdentity or ClientSecret entry."));

        if (string.Equals(credential.SourceType, ClientCredentialOptions.SignedAssertionFromManagedIdentity, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(credential.ManagedIdentityClientId))
            {
                throw new InvalidOperationException(
                    $"{app.Source}:ClientCredentials:0:ManagedIdentityClientId is empty. Set it to the client ID of the user-assigned managed identity.");
            }

            var managedIdentity = ManagedIdentityApplicationBuilder
                .Create(ManagedIdentityId.WithUserAssignedClientId(credential.ManagedIdentityClientId));
            if (httpClientFactory is not null)
            {
                managedIdentity.WithHttpClientFactory(httpClientFactory);
            }
            // Built once: MSAL caches the managed identity token and only calls the endpoint again near expiry.
            var miApp = managedIdentity.Build();
            var audience = string.IsNullOrWhiteSpace(credential.TokenExchangeUrl) ? DefaultTokenExchangeUrl : credential.TokenExchangeUrl;

            return builder.WithClientAssertion(async (AssertionRequestOptions options) =>
            {
                var result = await miApp.AcquireTokenForManagedIdentity(audience)
                    .ExecuteAsync(options.CancellationToken)
                    .ConfigureAwait(false);
                return result.AccessToken;
            });
        }

        if (string.Equals(credential.SourceType, ClientCredentialOptions.ClientSecretSourceType, StringComparison.OrdinalIgnoreCase))
        {
            return builder.WithClientSecret(string.IsNullOrEmpty(credential.ClientSecret)
                ? throw new InvalidOperationException($"{app.Source}:ClientCredentials:0:ClientSecret is empty. Set it with dotnet user-secrets.")
                : credential.ClientSecret);
        }

        throw new InvalidOperationException(
            $"{app.Source}:ClientCredentials:0:SourceType '{credential.SourceType}' is not supported by this POC. " +
            "Use SignedAssertionFromManagedIdentity or ClientSecret.");
    }

    /// <summary>A short description for the startup log. Never includes the secret.</summary>
    public static string Describe(SsoAppOptions app)
    {
        var credential = app.ClientCredentials.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.SourceType));
        return credential?.SourceType switch
        {
            null => "no credential",
            var t when t.Equals(ClientCredentialOptions.SignedAssertionFromManagedIdentity, StringComparison.OrdinalIgnoreCase)
                => $"federated credential from managed identity {credential.ManagedIdentityClientId}",
            var t => t,
        };
    }
}
