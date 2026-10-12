using Microsoft.Agents.Hosting.AspNetCore;
using Microsoft.Extensions.Options;
using TeamsSsoBot;
using TeamsSsoBot.Sso;

var builder = WebApplication.CreateBuilder(args);

// Microsoft 365 Agents SDK. Outbound Bot Framework traffic uses Connections:ServiceConnection, the bot's identity:
// in Azure a user-assigned managed identity (AuthType UserManagedIdentity), on your computer an app registration
// with a client secret.
builder.AddAgentDefaults();
builder.AddAgent<SsoBot>();

// Inbound: validate the Bot Connector's token on /api/messages. The SDK turns this off in the Development
// environment unless forced; this bot forces it on everywhere (a local tunnel is public too). Only
// TokenValidation:Enabled=false turns it off, for tests.
builder.AddAgentAuthorization(
    b => b.Services.AddAgentAspNetAuthentication(BotIdentity.TokenValidation(b.Configuration)),
    forceEnable: builder.Configuration.GetValue("TokenValidation:Enabled", true));

// Adaptive Card Action.Execute -> silent Teams SSO -> on-behalf-of.
builder.Services.AddCardActionSso(builder.Configuration);
builder.Services.AddHttpClient(DownstreamApiClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<DownstreamApiClient>();
builder.Services.AddSingleton<CardActionHandlers>();

var app = builder.Build();

app.UseAgents();
app.MapDefaultAgentEndpoints(); // POST /api/messages

var sso = app.Services.GetRequiredService<IOptions<SsoOptions>>().Value;
var ssoApp = app.Services.GetRequiredService<IOptions<SsoAppOptions>>().Value;
var bot = BotIdentity.From(app.Configuration);
app.Logger.LogInformation(
    "Bot ready on /api/messages (bot identity: {BotAuthType} {BotClientId}, strategy: {Strategy}, OAuth connection: {Connection}, " +
    "SSO resource: {Resource}, SSO app: {SsoAppClientId} from {SsoAppSource} with {SsoAppCredential})",
    bot.AuthType, bot.ClientId, sso.TokenStrategy, sso.OAuthConnectionName, sso.BotAppIdUri,
    ssoApp.ClientId, ssoApp.Source, SsoAppCredential.Describe(ssoApp));

app.Run();
