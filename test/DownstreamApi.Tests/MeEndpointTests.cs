using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using TestSupport;

namespace DownstreamApi.Tests;

public sealed class MeEndpointTests : IDisposable
{
    private readonly TestTenant _tenant = new();
    private readonly DownstreamApiFactory _factory;
    private readonly HttpClient _client;

    public MeEndpointTests()
    {
        _factory = new DownstreamApiFactory(_tenant);
        _client = _factory.CreateClient();
    }

    private Task<HttpResponseMessage> GetMe(string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Accepts_v2_delegated_token_and_reports_user_and_calling_app()
    {
        var response = await GetMe(_tenant.DownstreamToken());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<MeResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(me);
        Assert.Equal("Adele Vance", me.User.Name);
        Assert.Equal("adele@contoso.com", me.User.Username);
        Assert.Equal(TestTenant.UserOid, me.User.Oid);
        Assert.Equal(TestTenant.ApiClientId, me.Token.Aud);
        Assert.Equal("access_as_user", me.Token.Scp);
        Assert.Equal(TestTenant.SsoAppClientId, me.Token.CallingAppId); // the OBO caller
    }

    [Fact]
    public async Task Accepts_v1_token_with_app_id_uri_audience()
    {
        var response = await GetMe(_tenant.DownstreamToken(TestTenant.AsV1(TestTenant.ApiAppIdUri)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var me = await response.Content.ReadFromJsonAsync<MeResponse>(TestContext.Current.CancellationToken);
        Assert.Equal("adele@contoso.com", me!.User.Username);
        Assert.Equal(TestTenant.SsoAppClientId, me.Token.CallingAppId);
    }

    [Fact]
    public async Task Rejects_request_without_token() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMe(null)).StatusCode);

    [Fact]
    public async Task Rejects_the_raw_teams_sso_token_meant_for_the_bot() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMe(_tenant.SsoToken())).StatusCode);

    [Fact]
    public async Task Rejects_token_from_another_tenant()
    {
        const string other = "99999999-9999-9999-9999-999999999999";
        var token = _tenant.DownstreamToken(c =>
        {
            c["iss"] = $"https://login.microsoftonline.com/{other}/v2.0";
            c["tid"] = other;
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMe(token)).StatusCode);
    }

    [Fact]
    public async Task Rejects_expired_token()
    {
        var token = _tenant.DownstreamToken(c =>
        {
            c["nbf"] = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds();
            c["exp"] = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();
        });
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMe(token)).StatusCode);
    }

    [Fact]
    public async Task Rejects_token_signed_by_unknown_key()
    {
        using var rsa = RSA.Create(2048);
        var token = _tenant.Mint(new() { ["aud"] = TestTenant.ApiClientId }, key: new RsaSecurityKey(rsa) { KeyId = "test-key" });
        Assert.Equal(HttpStatusCode.Unauthorized, (await GetMe(token)).StatusCode);
    }

    [Fact]
    public async Task Rejects_app_only_token_with_403()
    {
        var token = _tenant.DownstreamToken(c =>
        {
            c.Remove("scp");
            c["roles"] = new[] { "Data.Read.All" };
        });
        Assert.Equal(HttpStatusCode.Forbidden, (await GetMe(token)).StatusCode);
    }

    [Fact]
    public async Task Rejects_user_token_without_required_scope_with_403()
    {
        var token = _tenant.DownstreamToken(c => c["scp"] = "User.Read");
        Assert.Equal(HttpStatusCode.Forbidden, (await GetMe(token)).StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        _tenant.Dispose();
    }
}
