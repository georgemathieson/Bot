using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Identity.Client;
using Microsoft.IdentityModel.JsonWebTokens;
using TestSupport;

namespace Bot.Tests;

/// <summary>
/// The two Microsoft endpoints MSAL calls for OBO with a managed identity credential, at the HTTP level:
/// <list type="bullet">
/// <item>The managed identity endpoint (IMDS): returns a token for <c>api://AzureADTokenExchange</c>. MSAL caches
/// managed identity tokens for the whole process, so only the first request per managed identity reaches it.</item>
/// <item>Entra's token endpoint: checks the OBO request like Entra does for this setup (client = SSO app,
/// credential = the managed identity token via the federated credential, assertion audience = SSO app) and
/// returns a downstream token for the same user.</item>
/// </list>
/// </summary>
internal sealed class FakeEntraHttp(TestTenant tenant) : HttpMessageHandler, IMsalHttpClientFactory
{
    public const string ManagedIdentityToken = "managed-identity-token-for-AzureADTokenExchange";
    public const string ClientSecret = "local-dev-secret";
    private const string ImdsHost = "169.254.169.254";

    public List<Uri> ManagedIdentityRequests { get; } = [];
    public List<Dictionary<string, string>> TokenRequests { get; } = [];
    public List<Uri> UnexpectedRequests { get; } = [];

    /// <summary>The next token request fails with this Entra error, e.g. ("invalid_grant", "AADSTS65001: ...").</summary>
    public (string Error, string Description)? NextError { get; set; }

    public HttpClient GetHttpClient() => new(this, disposeHandler: false);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Host == ImdsHost && uri.AbsolutePath == "/metadata/identity/oauth2/token")
        {
            ManagedIdentityRequests.Add(uri);
            return Json(HttpStatusCode.OK, new
            {
                access_token = ManagedIdentityToken,
                expires_on = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds().ToString(),
                resource = Query(uri, "resource"),
                token_type = "Bearer",
                client_id = Query(uri, "client_id"),
            });
        }

        if (uri.Host == ImdsHost)
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound); // other IMDS probes: not on this "host"
        }

        if (uri.Host == "login.microsoftonline.com" && uri.AbsolutePath == $"/{TestTenant.TenantId}/oauth2/v2.0/token")
        {
            var form = (await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(kv => WebUtility.UrlDecode(kv[0]), kv => WebUtility.UrlDecode(kv.ElementAtOrDefault(1) ?? string.Empty));
            TokenRequests.Add(form);
            return Obo(form);
        }

        if (uri.Host == "login.microsoftonline.com" && uri.AbsolutePath == "/common/discovery/instance")
        {
            // MSAL's instance discovery (it caches the answer for the whole process).
            return Json(HttpStatusCode.OK, new Dictionary<string, object>
            {
                ["tenant_discovery_endpoint"] = $"https://login.microsoftonline.com/{TestTenant.TenantId}/v2.0/.well-known/openid-configuration",
                ["api-version"] = "1.1",
                ["metadata"] = new[]
                {
                    new
                    {
                        preferred_network = "login.microsoftonline.com",
                        preferred_cache = "login.windows.net",
                        aliases = new[] { "login.microsoftonline.com", "login.windows.net", "login.microsoft.com", "sts.windows.net" },
                    },
                },
            });
        }

        UnexpectedRequests.Add(uri);
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage Obo(Dictionary<string, string> form)
    {
        if (NextError is { } next)
        {
            NextError = null;
            return Error(next.Error, next.Description);
        }

        var usesManagedIdentity = form.GetValueOrDefault("client_assertion") == ManagedIdentityToken
            && form.GetValueOrDefault("client_assertion_type") == "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";
        var usesSecret = form.GetValueOrDefault("client_secret") == ClientSecret;
        if (form.GetValueOrDefault("client_id") != TestTenant.SsoAppClientId || !(usesManagedIdentity || usesSecret))
        {
            return Error("invalid_client", "AADSTS70021: No matching federated identity record found for presented assertion.");
        }

        var assertion = new JsonWebToken(form["assertion"]);
        var aud = assertion.Audiences.Single();
        if (form.GetValueOrDefault("requested_token_use") != "on_behalf_of"
            || (aud != TestTenant.SsoAppClientId && aud != TestTenant.BotAppIdUri))
        {
            return Error("invalid_grant", "AADSTS50013: Assertion failed signature validation.");
        }

        return Json(HttpStatusCode.OK, new
        {
            token_type = "Bearer",
            scope = form["scope"],
            expires_in = 3600,
            ext_expires_in = 3600,
            access_token = tenant.DownstreamToken(c =>
            {
                c["oid"] = assertion.GetPayloadValue<string>("oid");
                c["name"] = assertion.GetPayloadValue<string>("name");
            }),
        });
    }

    public static string? Query(Uri uri, string name) =>
        uri.Query.TrimStart('?').Split('&')
            .Select(pair => pair.Split('=', 2))
            .Where(kv => kv[0] == name)
            .Select(kv => WebUtility.UrlDecode(kv.ElementAtOrDefault(1)))
            .FirstOrDefault();

    private static HttpResponseMessage Error(string error, string description) =>
        Json(HttpStatusCode.BadRequest, new { error, error_description = description });

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
