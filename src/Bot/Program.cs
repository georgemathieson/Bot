using Microsoft.Extensions.Options;
using Microsoft.Teams.Apps;
using TeamsSsoBot;
using TeamsSsoBot.Sso;

var builder = WebApplication.CreateBuilder(args);

// Teams SDK: authenticates Bot Framework traffic on /api/messages and outbound calls, using the
// "AzureAd" section (the bot's app registration).
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
    "Bot ready on /api/messages (strategy: {Strategy}, OAuth connection: {Connection}, SSO resource: {Resource})",
    sso.TokenStrategy, sso.OAuthConnectionName, sso.BotAppIdUri);

app.Run();
