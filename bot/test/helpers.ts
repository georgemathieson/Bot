import { createLocalJWKSet, exportJWK, generateKeyPair, SignJWT, type CryptoKey, type JWTPayload } from 'jose';
import { vi } from 'vitest';
import type { CardActionContext, TokenServiceClient } from '../src/sso/index.js';

export const TENANT_ID = '11111111-1111-1111-1111-111111111111';
export const API_CLIENT_ID = '22222222-2222-2222-2222-222222222222';
export const BOT_CLIENT_ID = '33333333-3333-3333-3333-333333333333';
export const USER_OID = '44444444-4444-4444-4444-444444444444';
export const BOT_APP_ID_URI = `api://botid-${BOT_CLIENT_ID}`;
export const TEAMS_DESKTOP_CLIENT_ID = '1fec8e78-bce4-4aaf-ab1b-5451cc387264';
export const SIGN_IN_LINK = 'https://token.botframework.com/api/oauth/signin?signin=abc';

/** A fake Entra tenant: an RSA key, its JWKS, and token minting helpers. */
export async function createTestIssuer() {
  const { publicKey, privateKey } = await generateKeyPair('RS256');
  const jwk = { ...(await exportJWK(publicKey)), kid: 'test-key', alg: 'RS256', use: 'sig' };
  const jwks = createLocalJWKSet({ keys: [jwk] });

  const sign = (claims: JWTPayload, opts: { expiresIn?: string | number; key?: CryptoKey } = {}) =>
    new SignJWT(claims)
      .setProtectedHeader({ alg: 'RS256', kid: 'test-key', typ: 'JWT' })
      .setIssuedAt()
      .setExpirationTime(opts.expiresIn ?? '1h')
      .sign(opts.key ?? privateKey);

  const base = {
    ver: '2.0',
    iss: `https://login.microsoftonline.com/${TENANT_ID}/v2.0`,
    tid: TENANT_ID,
    oid: USER_OID,
    name: 'Adele Vance',
    preferred_username: 'adele@contoso.com',
    scp: 'access_as_user',
  };

  return {
    jwks,
    sign,
    /** What Teams sends back after silent SSO: issued to the Teams client, for the bot's API. */
    ssoToken: (overrides: JWTPayload = {}) => sign({ ...base, aud: BOT_CLIENT_ID, azp: TEAMS_DESKTOP_CLIENT_ID, ...overrides }),
    /** What OBO returns: issued to the bot, for the downstream API. */
    downstreamToken: (overrides: JWTPayload = {}) => sign({ ...base, aud: API_CLIENT_ID, azp: BOT_CLIENT_ID, ...overrides }),
  };
}

export function fakeTokenService(overrides: Partial<TokenServiceClient> = {}): TokenServiceClient {
  return {
    getSignInResource: vi.fn(async () => ({
      signInLink: SIGN_IN_LINK,
      tokenExchangeResource: { id: 'ter-1', uri: BOT_APP_ID_URI, providerId: 'aad-provider' },
    })),
    getUserToken: vi.fn(async () => undefined),
    exchangeToken: vi.fn(async () => undefined),
    signOut: vi.fn(async () => undefined),
    ...overrides,
  };
}

/** Minimal `adaptiveCard/action` invoke activity, as Teams sends it. */
export function cardActionActivity(value: Record<string, unknown> = {}) {
  return {
    type: 'invoke' as const,
    name: 'adaptiveCard/action' as const,
    id: 'activity-1',
    channelId: 'msteams' as const,
    serviceUrl: 'https://smba.trafficmanager.net/teams/',
    from: { id: '29:user-1', name: 'Adele Vance', aadObjectId: USER_OID },
    recipient: { id: `28:${BOT_CLIENT_ID}`, name: 'bot' },
    conversation: { id: 'a:conversation-1', conversationType: 'personal' as const, tenantId: TENANT_ID },
    channelData: { tenant: { id: TENANT_ID } },
    value: {
      action: { type: 'Action.Execute', verb: 'callApi', data: { action: 'callApi' } },
      trigger: 'manual',
      ...value,
    },
  };
}

/** Just enough of the SDK's activity context for withUserToken(). */
export function fakeContext(value: Record<string, unknown> = {}) {
  return {
    activity: cardActionActivity(value),
    log: { info: vi.fn(), warn: vi.fn(), error: vi.fn(), debug: vi.fn() },
  } as unknown as CardActionContext;
}
