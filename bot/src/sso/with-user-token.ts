import { randomUUID } from 'node:crypto';
import type {
  AdaptiveCardActionLoginResponse,
  AdaptiveCardActionPreconditionFailedResponse,
  AdaptiveCardActionResponse,
  IAdaptiveCardActionInvokeActivity,
} from '@microsoft/teams.api';
import type { IActivityContext } from '@microsoft/teams.apps';
import { InteractionRequiredError, type DownstreamTokenStrategy, type TokenSource } from './downstream-token.js';
import type { EntraAccessTokenClaims } from './entra-jwt.js';
import type { SsoTokenValidator } from './sso-token.js';
import type { TokenServiceClient } from './token-service.js';

export type CardActionContext = IActivityContext<IAdaptiveCardActionInvokeActivity>;

export interface UserToken {
  /** Access token for the downstream API. Never log or display it. */
  accessToken: string;
  source: TokenSource;
  /** Claims of the Teams SSO token, when this click went through SSO. */
  ssoClaims?: EntraAccessTokenClaims;
}

export type AuthenticatedCardActionHandler = (
  ctx: CardActionContext,
  token: UserToken,
) => Promise<AdaptiveCardActionResponse>;

export interface WithUserTokenOptions {
  connectionName: string;
  /** Must match the OAuth connection's Token Exchange URL and manifest webApplicationInfo.resource. */
  botAppIdUri: string;
  tokenService: TokenServiceClient;
  strategy: DownstreamTokenStrategy;
  validateSsoToken: SsoTokenValidator;
  signInText?: string;
}

/**
 * Wraps an Adaptive Card `Action.Execute` handler so it receives a token for the downstream API,
 * obtained *silently* through Teams SSO + on-behalf-of. This is the Universal Actions auth protocol:
 *
 *   1. Click  -> invoke `adaptiveCard/action` (no token).
 *   2. Bot    -> 401 `application/vnd.microsoft.activity.loginRequest` carrying a `tokenExchangeResource`.
 *   3. Teams  -> silently gets an Entra token for the bot's API (`api://botid-...`), no popup when the
 *                Teams clients are pre-authorized and consent is granted, and re-sends the same invoke
 *                with `value.authentication.token`.
 *   4. Bot    -> validates that token, exchanges it on-behalf-of the user for a downstream token,
 *                runs the handler, and returns the updated card.
 *
 * Fallbacks: if the exchange needs user interaction (consent / MFA), the bot replies with a loginRequest
 * *without* SSO info, so Teams shows a Sign in button; once the user signs in, Teams re-sends the invoke
 * with the magic code in `value.state`. If the SSO token itself is unusable, the bot replies 412, which
 * also makes Teams fall back to the Sign in button.
 */
export function withUserToken(options: WithUserTokenOptions, handler: AuthenticatedCardActionHandler) {
  const { tokenService, strategy, validateSsoToken, connectionName } = options;

  const loginRequest = async (ctx: CardActionContext, withSso: boolean): Promise<AdaptiveCardActionLoginResponse> => {
    const resource = await tokenService.getSignInResource(ctx);
    let tokenExchangeResource = resource.tokenExchangeResource;
    if (withSso && !tokenExchangeResource) {
      ctx.log.warn(
        `OAuth connection "${connectionName}" returned no tokenExchangeResource - set its Token Exchange URL to ` +
          `${options.botAppIdUri}. Using the configured BOT_APP_ID_URI so SSO can still be attempted.`,
      );
      tokenExchangeResource = { id: randomUUID(), uri: options.botAppIdUri };
    } else if (withSso && tokenExchangeResource?.uri && tokenExchangeResource.uri !== options.botAppIdUri) {
      ctx.log.warn(
        `OAuth connection Token Exchange URL (${tokenExchangeResource.uri}) differs from BOT_APP_ID_URI ` +
          `(${options.botAppIdUri}); Teams will request a token for the connection's value.`,
      );
    }
    return {
      statusCode: 401,
      type: 'application/vnd.microsoft.activity.loginRequest',
      value: {
        text: options.signInText ?? 'Sign in to continue',
        connectionName,
        tokenExchangeResource: withSso ? tokenExchangeResource : undefined,
        buttons: [{ type: 'signin', title: 'Sign in', text: 'Sign in', value: resource.signInLink }],
      },
    };
  };

  return async (ctx: CardActionContext): Promise<AdaptiveCardActionResponse> => {
    const { value } = ctx.activity;

    // Back from the nominal (button) sign-in: Teams sends the magic code in `state`.
    if (value.state) {
      const accessToken = await tokenService.getUserToken(ctx, value.state);
      if (!accessToken) {
        ctx.log.warn('Sign-in magic code could not be redeemed; asking the user to sign in again');
        return loginRequest(ctx, false);
      }
      return handler(ctx, { accessToken, source: 'sign-in-fallback' });
    }

    // Teams completed silent SSO and re-sent the action with the token.
    const ssoToken = value.authentication?.token;
    if (ssoToken) {
      let ssoClaims: EntraAccessTokenClaims;
      try {
        ssoClaims = await validateSsoToken(ssoToken, ctx.activity.from.aadObjectId);
      } catch (err) {
        ctx.log.warn(`Rejected SSO token: ${(err as Error).message}`);
        return preconditionFailed('The single sign-on token could not be validated');
      }

      try {
        const { accessToken, source } = await strategy.exchangeSsoToken(ctx, ssoToken);
        return handler(ctx, { accessToken, source, ssoClaims });
      } catch (err) {
        if (err instanceof InteractionRequiredError) {
          ctx.log.info(`Silent exchange needs user interaction, falling back to sign-in: ${err.message}`);
          return loginRequest(ctx, false);
        }
        throw err;
      }
    }

    // Already have a downstream token (token-service strategy only).
    const cached = await strategy.getCachedToken(ctx);
    if (cached) {
      return handler(ctx, { accessToken: cached, source: 'token-service-cache' });
    }

    // No token yet: ask Teams to perform silent SSO.
    return loginRequest(ctx, true);
  };
}

function preconditionFailed(message: string): AdaptiveCardActionPreconditionFailedResponse {
  return {
    statusCode: 412,
    type: 'application/vnd.microsoft.error.preconditionFailed',
    value: { code: '412', message, innerHttpError: { statusCode: 412, body: null } },
  };
}
