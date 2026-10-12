using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TeamsSsoBot.Sso;
using TestSupport;

namespace Bot.Tests;

/// <summary>/api/messages only accepts requests with a valid Bot Connector token.</summary>
public sealed class InboundAuthTests
{
    [Theory]
    [InlineData("Development")] // the Agents SDK would turn validation off here unless forced
    [InlineData("Production")]
    public async Task Requests_without_a_Bot_Connector_token_are_rejected(string environment)
    {
        using var factory = new WebApplicationFactory<CardActionSso>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Connections:ServiceConnection:Settings:AuthType", "UserManagedIdentity");
            builder.UseSetting("Connections:ServiceConnection:Settings:ClientId", TestTenant.BotClientId);
            builder.UseSetting("Connections:ServiceConnection:Settings:TenantId", TestTenant.TenantId);
            builder.UseSetting("SsoApp:ClientId", TestTenant.SsoAppClientId);
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/messages",
            new { type = "message", text = "hi", channelId = "msteams", serviceUrl = "https://smba.trafficmanager.net/teams/" },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
