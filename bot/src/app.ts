import { AuthError } from '@azure/msal-node';
import type { AdaptiveCardActionCardResponse, AdaptiveCardActionErrorResponse } from '@microsoft/teams.api';
import { App, type AppOptions, type IPlugin } from '@microsoft/teams.apps';
import type { IAdaptiveCard } from '@microsoft/teams.cards';
import { CALL_API_VERB, errorCard, resultCard, welcomeCard } from './cards.js';
import type { BotConfig } from './config.js';
import { createDownstreamApiClient, DownstreamApiError, type DownstreamApiClient } from './downstream-api.js';
import {
  createOboStrategy,
  createSsoTokenValidator,
  createTokenServiceClient,
  createTokenServiceStrategy,
  httpStatusOf,
  withUserToken,
  type DownstreamTokenStrategy,
  type SsoTokenValidator,
  type TokenServiceClient,
} from './sso/index.js';

/** Collaborators that talk to Entra / Bot Framework / the API; overridable for tests. */
export interface BotDeps {
  tokenService?: TokenServiceClient;
  strategy?: DownstreamTokenStrategy;
  validateSsoToken?: SsoTokenValidator;
  callDownstreamApi?: DownstreamApiClient;
}

export function createBot(config: BotConfig, deps: BotDeps = {}, appOptions: AppOptions<IPlugin> = {}) {
  const tokenService = deps.tokenService ?? createTokenServiceClient(config.oauthConnectionName);
  const strategy =
    deps.strategy ??
    (config.tokenStrategy === 'obo'
      ? createOboStrategy({
          clientId: config.clientId,
          clientSecret: config.clientSecret,
          tenantId: config.tenantId,
          scopes: config.downstream.scopes,
        })
      : createTokenServiceStrategy(tokenService));
  const validateSsoToken =
    deps.validateSsoToken ??
    createSsoTokenValidator({
      tenantId: config.tenantId,
      clientId: config.clientId,
      botAppIdUri: config.botAppIdUri,
      botSsoScope: config.botSsoScope,
    });
  const callDownstreamApi = deps.callDownstreamApi ?? createDownstreamApiClient(config.downstream.url);

  const app = new App({
    clientId: config.clientId,
    clientSecret: config.clientSecret,
    tenantId: config.tenantId,
    ...appOptions,
  });

  app.on('install.add', async ({ send }) => {
    await send(welcomeCard());
  });

  app.on('message', async (ctx) => {
    const text = ctx.activity.text?.trim().toLowerCase();
    if (text === 'signout' || text === 'sign out') {
      // Only the token-service strategy and the sign-in fallback store tokens in the token store.
      await tokenService.signOut(ctx);
      await ctx.send('Signed out: any token cached in the Bot Framework token store was removed.');
      return;
    }
    await ctx.send(welcomeCard());
  });

  const callApi = withUserToken(
    {
      connectionName: config.oauthConnectionName,
      botAppIdUri: config.botAppIdUri,
      tokenService,
      strategy,
      validateSsoToken,
    },
    async (ctx, token) => {
      try {
        const result = await callDownstreamApi(token.accessToken);
        ctx.log.info(`Downstream API accepted the token for ${result.user.username ?? result.user.oid} (${token.source})`);
        return cardResponse(resultCard(result, token, config.showDiagnostics));
      } catch (err) {
        ctx.log.error(`Downstream API call failed: ${describeError(err)}`);
        return cardResponse(errorCard('The downstream API call failed', detail(err, config.showDiagnostics)));
      }
    },
  );

  app.on(`card.action.${CALL_API_VERB}`, async (ctx) => {
    try {
      return await callApi(ctx);
    } catch (err) {
      // Not an "ask the user" situation: usually configuration (wrong secret, scope, audience...).
      ctx.log.error(`Could not obtain a token for the downstream API: ${describeError(err)}`);
      return cardResponse(errorCard('Could not get a token for the downstream API', detail(err, config.showDiagnostics)));
    }
  });

  // Any other Action.Execute verb.
  app.on('card.action', async ({ activity }): Promise<AdaptiveCardActionErrorResponse> => ({
    statusCode: 400,
    type: 'application/vnd.microsoft.error',
    value: {
      code: 'BadRequest',
      message: `Unknown card action "${activity.value.action.verb ?? activity.value.action.data?.action}"`,
      innerHttpError: { statusCode: 400, body: null },
    },
  }));

  return app;
}

function cardResponse(card: IAdaptiveCard): AdaptiveCardActionCardResponse {
  return { statusCode: 200, type: 'application/vnd.microsoft.card.adaptive', value: card };
}

/** Error text for the card. MSAL/Entra messages carry the AADSTS code, which is what you need to debug. */
function detail(err: unknown, showDiagnostics: boolean): string {
  return showDiagnostics ? describeError(err) : 'Ask your administrator to check the bot logs.';
}

/**
 * A one-line description that is safe to log. Never log the raw error object: axios errors from the
 * token service carry the request config, including the bot's own bearer token.
 */
function describeError(err: unknown): string {
  if (err instanceof AuthError) return `${err.errorCode}: ${err.errorMessage}`;
  if (err instanceof DownstreamApiError) return err.message;
  const status = httpStatusOf(err);
  const message = err instanceof Error ? err.message : String(err);
  return status ? `HTTP ${status}: ${message}` : message;
}
