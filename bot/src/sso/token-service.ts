import type { SignInUrlResponse } from '@microsoft/teams.api';
import type { IActivityContext } from '@microsoft/teams.apps';

/**
 * The Bot Framework token service calls this bot uses, wrapped so they can be stubbed in tests.
 * The service is backed by the OAuth connection on the Azure Bot resource.
 */
export interface TokenServiceClient {
  /** Sign-in link (nominal fallback) + tokenExchangeResource (SSO) for the OAuth connection. */
  getSignInResource(ctx: IActivityContext): Promise<SignInUrlResponse>;
  /** Token cached in the token store, or redeem a sign-in magic code. Undefined when there is none. */
  getUserToken(ctx: IActivityContext, magicCode?: string): Promise<string | undefined>;
  /** Ask the token service to OBO-exchange a Teams SSO token for the connection's scopes. */
  exchangeToken(ctx: IActivityContext, ssoToken: string): Promise<string | undefined>;
  signOut(ctx: IActivityContext): Promise<void>;
}

export function createTokenServiceClient(connectionName: string): TokenServiceClient {
  const ids = (ctx: IActivityContext) => ({
    userId: ctx.activity.from.id,
    channelId: ctx.activity.channelId,
    connectionName,
  });

  return {
    async getSignInResource(ctx) {
      // Same state shape the SDK's own OAuth flow sends; the token service uses it to route the
      // sign-in result back to this conversation.
      const state = Buffer.from(
        JSON.stringify({
          connectionName,
          conversation: { ...ctx.ref },
          relatesTo: ctx.activity.relatesTo,
          msAppId: ctx.appId,
        }),
      ).toString('base64');
      return ctx.api.bots.signIn.getResource({ state });
    },

    async getUserToken(ctx, magicCode) {
      try {
        const res = await ctx.api.users.getToken({ ...ids(ctx), code: magicCode });
        return res.token || undefined;
      } catch (err) {
        // 404 = no token stored for this user/connection (or the magic code was not valid).
        if (httpStatusOf(err) === 404) return undefined;
        throw err;
      }
    },

    async exchangeToken(ctx, ssoToken) {
      try {
        const res = await ctx.api.users.exchangeToken({ ...ids(ctx), exchangeRequest: { token: ssoToken } });
        return res.token || undefined;
      } catch (err) {
        // 400/404/412 = the service could not exchange (typically consent is missing). The SDK's own
        // OAuth flow treats these as "fall back to interactive sign-in"; so do we.
        const status = httpStatusOf(err);
        if (status === 400 || status === 404 || status === 412) return undefined;
        throw err;
      }
    },

    async signOut(ctx) {
      await ctx.api.users.signOut(ids(ctx));
    },
  };
}

/** HTTP status from an axios-style error, without depending on axios directly. */
export function httpStatusOf(err: unknown): number | undefined {
  if (typeof err !== 'object' || err === null) return undefined;
  const e = err as { status?: unknown; response?: { status?: unknown } };
  const status = e.status ?? e.response?.status;
  return typeof status === 'number' ? status : undefined;
}
