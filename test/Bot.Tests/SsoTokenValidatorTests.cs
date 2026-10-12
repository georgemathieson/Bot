using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using TeamsSsoBot.Sso;
using TestSupport;

namespace Bot.Tests;

public sealed class SsoTokenValidatorTests : IDisposable
{
    private readonly TestTenant _tenant = new();
    private readonly SsoTokenValidator _validator;

    public SsoTokenValidatorTests()
    {
        _validator = new SsoTokenValidator(
            Options.Create(new SsoAppOptions { TenantId = TestTenant.TenantId, ClientId = TestTenant.SsoAppClientId }),
            Options.Create(new SsoOptions { BotAppIdUri = TestTenant.BotAppIdUri, BotSsoScope = "access_as_user" }),
            _tenant.ConfigurationManager);
    }

    private Task<SsoTokenClaims> Validate(string token, string? oid = TestTenant.UserOid) =>
        _validator.ValidateAsync(token, oid, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Accepts_the_token_Teams_obtains_for_the_bot()
    {
        var claims = await Validate(_tenant.SsoToken());

        Assert.Equal(TestTenant.SsoAppClientId, claims.Audience);
        Assert.Equal("access_as_user", claims.Scope);
        Assert.Equal(TestTenant.TeamsDesktopClientId, claims.AuthorizedParty);
        Assert.Equal(TestTenant.UserOid, claims.ObjectId);
    }

    [Fact]
    public async Task Accepts_a_v1_sso_token_with_the_botid_app_id_uri_audience()
    {
        var claims = await Validate(_tenant.SsoToken(TestTenant.AsV1(TestTenant.BotAppIdUri)));

        Assert.Equal(TestTenant.BotAppIdUri, claims.Audience);
        Assert.Equal(TestTenant.TeamsDesktopClientId, claims.AuthorizedParty);
    }

    [Fact]
    public async Task Rejects_a_token_for_a_different_user_than_the_one_who_clicked()
    {
        var ex = await Assert.ThrowsAsync<SsoTokenValidationException>(() => Validate(_tenant.SsoToken(), TestTenant.OtherUserOid));
        Assert.Contains("different user", ex.Message);
    }

    [Fact]
    public async Task Rejects_a_token_meant_for_another_api()
    {
        var ex = await Assert.ThrowsAsync<SsoTokenValidationException>(() => Validate(_tenant.DownstreamToken()));
        Assert.Contains("IDX10214", ex.Message); // audience validation failed (quoted in docs/troubleshooting.md)
    }

    [Fact]
    public async Task Rejects_a_token_without_the_access_as_user_scope()
    {
        var ex = await Assert.ThrowsAsync<SsoTokenValidationException>(() => Validate(_tenant.SsoToken(c => c["scp"] = "User.Read")));
        Assert.Contains("access_as_user", ex.Message);
    }

    [Fact]
    public async Task Rejects_a_token_from_another_tenant()
    {
        const string other = "99999999-9999-9999-9999-999999999999";
        await Assert.ThrowsAsync<SsoTokenValidationException>(() => Validate(_tenant.SsoToken(c =>
        {
            c["iss"] = $"https://login.microsoftonline.com/{other}/v2.0";
            c["tid"] = other;
        })));
    }

    [Fact]
    public async Task Rejects_an_expired_token() =>
        await Assert.ThrowsAsync<SsoTokenValidationException>(() => Validate(_tenant.SsoToken(c =>
        {
            c["nbf"] = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds();
            c["exp"] = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();
        })));

    [Fact]
    public async Task Refreshes_signing_keys_once_when_Entra_has_rotated_them()
    {
        using var rotated = new TestTenant(keyId: "rotated-key"); // a key the validator's cached metadata doesn't have yet
        var metadata = new RotatingConfigurationManager(_tenant.Configuration, rotated.Configuration);
        var validator = new SsoTokenValidator(
            Options.Create(new SsoAppOptions { TenantId = TestTenant.TenantId, ClientId = TestTenant.SsoAppClientId }),
            Options.Create(new SsoOptions { BotAppIdUri = TestTenant.BotAppIdUri }),
            metadata);

        var claims = await validator.ValidateAsync(rotated.SsoToken(), TestTenant.UserOid, TestContext.Current.CancellationToken);

        Assert.Equal(TestTenant.UserOid, claims.ObjectId);
        Assert.Equal(1, metadata.Refreshes);
    }

    private sealed class RotatingConfigurationManager(OpenIdConnectConfiguration before, OpenIdConnectConfiguration after)
        : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public int Refreshes { get; private set; }

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            Task.FromResult(Refreshes == 0 ? before : after);

        public void RequestRefresh() => Refreshes++;
    }

    public void Dispose() => _tenant.Dispose();
}
