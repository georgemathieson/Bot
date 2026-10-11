using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;

// The "other service": protected by its OWN Entra app registration. It only accepts delegated
// (user) access tokens issued for it, which is exactly what the bot obtains through on-behalf-of.
var builder = WebApplication.CreateBuilder(args);

// Microsoft.Identity.Web validates signature (tenant keys), issuer (v1 or v2 endpoint), audience
// (ClientId or api://ClientId) and lifetime. Keep the raw claim names ("scp", "oid", ...).
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o => o.MapInboundClaims = false);
builder.Services.AddAuthorization();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Returns who the API thinks is calling. In the POC this proves the token the bot obtained via OBO
// is a real delegated token for the Teams user:
//  - oid / name / preferred_username identify the user
//  - azp (v2) / appid (v1) is the bot's client ID: the bot called us *on behalf of* that user
// RequireScope demands the delegated scope in "scp", so app-only tokens (roles, no scp) get 403.
app.MapGet("/api/me", (ClaimsPrincipal user) =>
    {
        string? Claim(params string[] types) =>
            types.Select(t => user.FindFirst(t)?.Value).FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var name = Claim("name");
        var username = Claim("preferred_username", "upn");
        var exp = long.TryParse(Claim("exp"), out var seconds) ? DateTimeOffset.FromUnixTimeSeconds(seconds) : (DateTimeOffset?)null;

        return Results.Ok(new MeResponse(
            $"Hello {name ?? username ?? "unknown user"}, the downstream API accepted your token.",
            new MeUser(name, username, Claim("oid"), Claim("tid")),
            new MeToken(Claim("ver"), Claim("aud"), Claim("scp"), Claim("azp", "appid"), exp)));
    })
    .RequireAuthorization()
    .RequireScope(app.Configuration["AzureAd:Scopes"] ?? "access_as_user");

app.Run();

public sealed record MeResponse(string Message, MeUser User, MeToken Token);
public sealed record MeUser(string? Name, string? Username, string? Oid, string? Tid);
public sealed record MeToken(string? Ver, string? Aud, string? Scp, string? CallingAppId, DateTimeOffset? ExpiresAt);

public partial class Program;
