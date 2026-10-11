import { AuthError, ConfidentialClientApplication, InteractionRequiredAuthError } from '@azure/msal-node';
import type { IActivityContext } from '@microsoft/teams.apps';
import type { TokenServiceClient } from './token-service.js';

/** How the downstream token was obtained. Shown on the result card so you can see which path ran. */
export type TokenSource = 'sso+obo' | 'sso+token-service' | 'token-service-cache' | 'sign-in-fallback';

/**
 * Turns the Teams SSO token (aud = the bot) into a token for the downstream API (aud = the API).
 */
export interface DownstreamTokenStrategy {
  readonly name: 'obo' | 'token-service';
  /** A downstream token that is already available without asking Teams for SSO, if any. */
  getCachedToken(ctx: IActivityContext): Promise<string | undefined>;
  /**
   * Exchange the SSO token. Throws {@link InteractionRequiredError} when the user has to see a
   * prompt (consent, MFA / Conditional Access step-up); any other error is a real failure.
   */
  exchangeSsoToken(ctx: IActivityContext, ssoToken: string): Promise<{ accessToken: string; source: TokenSource }>;
}

/** The exchange can only succeed after the user interacts (consents, completes MFA, ...). */
export class InteractionRequiredError extends Error {
  constructor(
    message: string,
    /** Claims challenge from Entra (Conditional Access), if any. */
    readonly claims?: string,
  ) {
    super(message);
    this.name = 'InteractionRequiredError';
  }
}

// Entra errors that a user can fix by signing in interactively. MSAL already classifies most of
// these as InteractionRequiredAuthError; the codes are a belt-and-braces check on the message.
const INTERACTIVE_AADSTS = [
  'AADSTS65001', // consent required
  'AADSTS50076', // MFA required (location/strong auth)
  'AADSTS50079', // MFA registration required
  'AADSTS50158', // external security challenge
  'AADSTS50173', // grant expired / revoked, re-sign-in needed
];

/**
 * Option A (default): the bot performs the OAuth 2.0 on-behalf-of exchange itself with MSAL.
 *
 * POST https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token
 *   grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer
 *   assertion=<Teams SSO token>  requested_token_use=on_behalf_of
 *   client_id=<bot>  client_secret=<bot secret>  scope=<downstream scopes>
 *
 * Stateless: nothing is stored in the Bot Framework token store. MSAL's in-memory cache is keyed by
 * the assertion, so repeated clicks that carry the same (still valid) SSO token don't hit Entra again.
 * For multiple instances, plug in a distributed MSAL cache.
 */
export function createOboStrategy(options: {
  clientId: string;
  clientSecret: string;
  tenantId: string;
  scopes: string[];
  authorityHost?: string;
  /** Inject a preconfigured MSAL app (tests, certificates, federated credentials). */
  msalApp?: Pick<ConfidentialClientApplication, 'acquireTokenOnBehalfOf'>;
}): DownstreamTokenStrategy {
  const msal =
    options.msalApp ??
    new ConfidentialClientApplication({
      auth: {
        clientId: options.clientId,
        clientSecret: options.clientSecret,
        authority: `${(options.authorityHost ?? 'https://login.microsoftonline.com').replace(/\/$/, '')}/${options.tenantId}`,
      },
    });

  return {
    name: 'obo',
    async getCachedToken() {
      // Stateless: always ask Teams for a (silent) SSO token. Teams caches it client-side.
      return undefined;
    },
    async exchangeSsoToken(_ctx, ssoToken) {
      try {
        const result = await msal.acquireTokenOnBehalfOf({ oboAssertion: ssoToken, scopes: options.scopes });
        if (!result?.accessToken) throw new Error('OBO returned no access token');
        return { accessToken: result.accessToken, source: 'sso+obo' };
      } catch (err) {
        if (isInteractionRequired(err)) {
          const e = err as AuthError & { claims?: string };
          throw new InteractionRequiredError(`${e.errorCode}: ${e.errorMessage}`, e.claims || undefined);
        }
        throw err;
      }
    },
  };
}

/**
 * Option B: let the Bot Framework token service do the OBO exchange, using the Azure Bot's OAuth
 * connection (its client ID/secret, Token Exchange URL and Scopes). The service also stores and
 * refreshes the resulting token, so later clicks skip the SSO round trip entirely.
 */
export function createTokenServiceStrategy(tokenService: TokenServiceClient): DownstreamTokenStrategy {
  return {
    name: 'token-service',
    getCachedToken: (ctx) => tokenService.getUserToken(ctx),
    async exchangeSsoToken(ctx, ssoToken) {
      const accessToken = await tokenService.exchangeToken(ctx, ssoToken);
      if (!accessToken) {
        throw new InteractionRequiredError('Token service could not exchange the SSO token (consent is usually missing)');
      }
      return { accessToken, source: 'sso+token-service' };
    },
  };
}

function isInteractionRequired(err: unknown): boolean {
  if (err instanceof InteractionRequiredAuthError) return true;
  if (err instanceof AuthError) {
    const text = `${err.errorCode} ${err.errorMessage} ${err.subError}`;
    return INTERACTIVE_AADSTS.some((code) => text.includes(code));
  }
  return false;
}
