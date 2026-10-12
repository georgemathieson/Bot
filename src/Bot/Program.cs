using Microsoft.Extensions.Options;
using Microsoft.Teams.Apps;
using TeamsSsoBot;
using TeamsSsoBot.Sso;

var builder = WebApplication.CreateBuilder(args);

// Teams SDK: authenticates Bot Framework traffic on /api/messages and outbound calls with the "AzureAd" section,
// the Azure Bot's identity. With no ClientCredentials there, the SDK uses the user-assigned managed identity whose
// client ID is AzureAd:ClientId (Azure Bot type "User-Assigned Managed Identity"). With a ClientSecret, it uses an
// app registration (for running on your computer).
builder.Services.AddTeamsBotApplication();

// Adaptive Card Action.Execute -> silent Teams SSO -> on-behalf-of.
builder.Services.AddCardActionSso(builder.Configuration);
builder.Services.AddHttpClient(DownstreamApiClient.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<DownstreamApiClient>();
builder.Services.AddSingleton<CardActionHandlers>();

var app = builder.Build();

var bot = app.UseTeamsBotApplication(); // POST /api/messages
var handlers = app.Services.GetRequiredService<CardActionHandlers>();
var sso = app.Services.GetRequiredService<IOptions<SsoOptions>>().Value;
var ssoApp = app.Services.GetRequiredService<IOptions<SsoAppOptions>>().Value;

bot.OnInstall(async (context, ct) =>
    await context.SendAsync(new MessageActivityInput().WithAdaptiveCardAttachment(Cards.Welcome()), ct));

bot.OnMessage(async (context, ct) =>
{
    if (string.Equals(context.Activity.Text?.Trim(), "signout", StringComparison.OrdinalIgnoreCase))
    {
        // Only the token-service strategy and the sign-in fallback store tokens in the token store.
        await context.Api.UserToken.SignOutAsync(context.Activity.From!.Id!, sso.OAuthConnectionName, context.Activity.ChannelId ?? "msteams", ct);
        await context.SendAsync("Signed out: any token cached in the Bot Framework token store was removed.", ct);
        return;
    }
    await context.SendAsync(new MessageActivityInput().WithAdaptiveCardAttachment(Cards.Welcome()), ct);
});

bot.OnAdaptiveCardAction((context, ct) => handlers.OnCardActionAsync(CardActionTurn.FromContext(context), ct));

app.Logger.LogInformation(
    "Bot ready on /api/messages (strategy: {Strategy}, OAuth connection: {Connection}, SSO resource: {Resource}, " +
    "SSO app: {SsoAppClientId} from {SsoAppSource} with {SsoAppCredential})",
    sso.TokenStrategy, sso.OAuthConnectionName, sso.BotAppIdUri, ssoApp.ClientId, ssoApp.Source, SsoAppCredential.Describe(ssoApp));

app.Run();
