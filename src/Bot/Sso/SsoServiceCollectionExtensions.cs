using Microsoft.Extensions.Options;

namespace TeamsSsoBot.Sso;

public static class SsoServiceCollectionExtensions
{
    /// <summary>Registers the Action.Execute SSO handshake and the configured SSO-to-downstream token strategy.</summary>
    public static IServiceCollection AddCardActionSso(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SsoAppOptions>().Configure(options => SsoAppOptions.Bind(configuration, options));
        services.AddOptions<DownstreamApiOptions>().Bind(configuration.GetSection(DownstreamApiOptions.SectionName));
        services.AddOptions<SsoOptions>()
            .Bind(configuration.GetSection(SsoOptions.SectionName))
            .PostConfigure<IOptions<SsoAppOptions>>((sso, ssoApp) =>
            {
                if (string.IsNullOrWhiteSpace(sso.BotAppIdUri))
                {
                    sso.BotAppIdUri = $"api://botid-{ssoApp.Value.ClientId}";
                }
            });

        services.AddSingleton<ITokenServiceClient, TokenServiceClient>();
        services.AddSingleton<ISsoTokenValidator>(sp => new SsoTokenValidator(
            sp.GetRequiredService<IOptions<SsoAppOptions>>(),
            sp.GetRequiredService<IOptions<SsoOptions>>()));
        services.AddSingleton<IOboTokenAcquirer>(sp => new MsalOboTokenAcquirer(sp.GetRequiredService<IOptions<SsoAppOptions>>()));
        services.AddSingleton<IDownstreamTokenStrategy>(sp =>
            sp.GetRequiredService<IOptions<SsoOptions>>().Value.TokenStrategy == TokenStrategy.TokenService
                ? ActivatorUtilities.CreateInstance<TokenServiceTokenStrategy>(sp)
                : ActivatorUtilities.CreateInstance<OboTokenStrategy>(sp));
        services.AddSingleton<CardActionSso>();
        return services;
    }
}
