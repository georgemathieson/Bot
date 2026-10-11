using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace TestSupport;

/// <summary>
/// Hosts the real downstream API in-process, with Microsoft.Identity.Web validating tokens against
/// <see cref="TestTenant"/>'s signing key instead of the real tenant's published keys.
/// </summary>
public sealed class DownstreamApiFactory(TestTenant tenant) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("AzureAd:Instance", "https://login.microsoftonline.com/");
        builder.UseSetting("AzureAd:TenantId", TestTenant.TenantId);
        builder.UseSetting("AzureAd:ClientId", TestTenant.ApiClientId);
        builder.UseSetting("AzureAd:Scopes", "access_as_user");

        builder.ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                // Offline: signing keys from the fake tenant, no metadata download.
                o.ConfigurationManager = tenant.ConfigurationManager;
                o.Configuration = tenant.Configuration;
                // Replace Microsoft.Identity.Web's issuer validator (which downloads instance
                // metadata) with the two issuers a single-tenant API accepts.
                o.TokenValidationParameters.IssuerValidator = null;
                o.TokenValidationParameters.ValidIssuers = [TestTenant.V2Issuer, TestTenant.V1Issuer];
            }));
    }
}
