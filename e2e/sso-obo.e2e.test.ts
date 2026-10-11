/**
 * End-to-end: real Teams SDK app (HTTP /api/messages) + real downstream API (JWT validation),
 * with only the parts that live in Microsoft's cloud simulated:
 *   - Entra ID          -> a local signing key + JWKS, and a fake MSAL OBO call that mints the downstream token
 *   - BF token service  -> a stub that returns a sign-in link + tokenExchangeResource
 *   - Teams client      -> this test, which re-sends the invoke with an SSO token after the 401
 */
import type { AddressInfo } from 'node:net';
import { createServer, type Server } from 'node:http';
import { AuthError } from '@azure/msal-node';
import { decodeJwt } from 'jose';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { createBot } from '../bot/src/app.js';
import type { BotConfig } from '../bot/src/config.js';
import { createOboStrategy, createSsoTokenValidator } from '../bot/src/sso/index.js';
import {
  API_CLIENT_ID,
  BOT_APP_ID_URI,
  BOT_CLIENT_ID,
  cardActionActivity,
  createTestIssuer,
  fakeTokenService,
  TENANT_ID,
  USER_OID,
} from '../bot/test/helpers.js';
import { createApp as createDownstreamApi } from '../downstream-api/src/app.js';
import { createEntraJwtVerifier } from '../downstream-api/src/entra-jwt.js';

let issuer: Awaited<ReturnType<typeof createTestIssuer>>;
let api: Server;
let bot: ReturnType<typeof createBot>;
let botUrl: string;
const oboCalls: { assertion: string; scopes: string[] }[] = [];
/** Set to make the next fake OBO call fail like Entra would. */
let nextOboError: Error | undefined;

beforeAll(async () => {
  issuer = await createTestIssuer();

  // Downstream API, validating tokens against the fake tenant's keys.
  api = createDownstreamApi(
    createEntraJwtVerifier({
      tenantId: TENANT_ID,
      audiences: [API_CLIENT_ID, `api://${API_CLIENT_ID}`],
      requiredScopes: ['access_as_user'],
      jwks: issuer.jwks,
    }),
  ).listen(0);
  await new Promise((resolve) => api.once('listening', resolve));

  const config: BotConfig = {
    clientId: BOT_CLIENT_ID,
    clientSecret: 'not-used',
    tenantId: TENANT_ID,
    botAppIdUri: BOT_APP_ID_URI,
    botSsoScope: 'access_as_user',
    oauthConnectionName: 'sso',
    downstream: {
      url: `http://127.0.0.1:${(api.address() as AddressInfo).port}/api/me`,
      scopes: [`api://${API_CLIENT_ID}/access_as_user`],
    },
    tokenStrategy: 'obo',
    showDiagnostics: true,
    port: await freePort(),
  };

  bot = createBot(
    config,
    {
      tokenService: fakeTokenService(),
      validateSsoToken: createSsoTokenValidator({ ...config, jwks: issuer.jwks }),
      strategy: createOboStrategy({
        ...config,
        scopes: config.downstream.scopes,
        // Stand-in for Entra's /token endpoint (grant_type=jwt-bearer, requested_token_use=on_behalf_of):
        // it checks the assertion was issued for the bot and returns a token for the downstream API
        // that carries the same user, with azp = the bot.
        msalApp: {
          acquireTokenOnBehalfOf: async ({ oboAssertion, scopes }) => {
            oboCalls.push({ assertion: oboAssertion, scopes });
            const error = nextOboError;
            nextOboError = undefined;
            if (error) throw error;
            const assertion = decodeJwt(oboAssertion);
            if (assertion.aud !== BOT_CLIENT_ID && assertion.aud !== BOT_APP_ID_URI) {
              throw new AuthError('invalid_grant', 'corr', 'AADSTS50013: Assertion audience does not match the client');
            }
            const { oid, name, preferred_username } = assertion;
            return { accessToken: await issuer.downstreamToken({ oid, name, preferred_username }) } as never;
          },
        },
      }),
    },
    // No Bot Connector JWT in a local test, and no app credentials so nothing calls out to Entra.
    { dangerouslyAllowUnauthenticatedRequests: true, clientId: undefined, clientSecret: undefined, tenantId: undefined },
  );
  await bot.start(config.port);
  botUrl = `http://127.0.0.1:${config.port}/api/messages`;
});

afterAll(async () => {
  await bot?.stop();
  await new Promise((resolve) => api?.close(resolve));
});

async function invoke(value: Record<string, unknown> = {}) {
  return post(cardActionActivity(value));
}

async function post(activity: object) {
  const res = await fetch(botUrl, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(activity),
  });
  return { status: res.status, body: (await res.json()) as { statusCode: number; type: string; value: any } };
}

describe('Action.Execute -> Teams SSO -> OBO -> downstream API', () => {
  it('click 1: bot asks Teams for an SSO token (401 loginRequest with tokenExchangeResource)', async () => {
    const { status, body } = await invoke();

    expect(status).toBe(200); // invoke responses travel in the HTTP body
    expect(body.statusCode).toBe(401);
    expect(body.type).toBe('application/vnd.microsoft.activity.loginRequest');
    expect(body.value.tokenExchangeResource.uri).toBe(BOT_APP_ID_URI);
    expect(body.value.buttons[0].type).toBe('signin');
  });

  it('click 1, retry: Teams re-sends with the SSO token; bot does OBO and the API sees the real user', async () => {
    const ssoToken = await issuer.ssoToken();
    const { body } = await invoke({ authentication: { id: 'ter-1', connectionName: 'sso', token: ssoToken } });

    expect(oboCalls.at(-1)).toEqual({ assertion: ssoToken, scopes: [`api://${API_CLIENT_ID}/access_as_user`] });
    expect(body.statusCode).toBe(200);
    expect(body.type).toBe('application/vnd.microsoft.card.adaptive');

    const text = JSON.stringify(body.value);
    expect(text).toContain('Hello Adele Vance, the downstream API accepted your token.');
    expect(text).toContain(USER_OID);
    expect(text).toContain('Teams SSO, then the bot ran on-behalf-of with MSAL');
    // The API saw a token for itself, requested by the bot - the OBO chain.
    const facts = body.value.body.filter((b: { type: string }) => b.type === 'FactSet').flatMap((b: any) => b.facts);
    expect(facts).toEqual(
      expect.arrayContaining([
        { title: 'aud', value: API_CLIENT_ID },
        { title: 'azp/appid (caller)', value: BOT_CLIENT_ID },
        { title: 'aud', value: BOT_CLIENT_ID },
        { title: 'azp/appid (caller)', value: '1fec8e78-bce4-4aaf-ab1b-5451cc387264 (Teams desktop/mobile)' },
      ]),
    );
  });

  it('rejects an SSO token that belongs to a different user (412 -> Teams falls back to sign-in)', async () => {
    const ssoToken = await issuer.ssoToken({ oid: '55555555-5555-5555-5555-555555555555' });
    const calls = oboCalls.length;
    const { body } = await invoke({ authentication: { id: 'ter-1', connectionName: 'sso', token: ssoToken } });

    expect(body.statusCode).toBe(412);
    expect(oboCalls.length).toBe(calls);
  });

  it('falls back to the Sign in button (no SSO info) when Entra says consent is missing', async () => {
    nextOboError = new AuthError('invalid_grant', 'corr', 'AADSTS65001: The user or administrator has not consented');
    const { body } = await invoke({ authentication: { id: 'ter-1', connectionName: 'sso', token: await issuer.ssoToken() } });

    expect(body.statusCode).toBe(401);
    expect(body.type).toBe('application/vnd.microsoft.activity.loginRequest');
    expect(body.value.tokenExchangeResource).toBeUndefined();
  });

  it('shows an error card with the Entra error when OBO fails for a configuration reason', async () => {
    nextOboError = new AuthError('invalid_client', 'corr', 'AADSTS7000215: Invalid client secret provided.');
    const { body } = await invoke({ authentication: { id: 'ter-1', connectionName: 'sso', token: await issuer.ssoToken() } });

    expect(body.statusCode).toBe(200);
    expect(body.type).toBe('application/vnd.microsoft.card.adaptive');
    expect(JSON.stringify(body.value)).toContain('invalid_client: AADSTS7000215: Invalid client secret provided.');
  });

  it('accepts a v1 SSO token (aud = api://botid-...) end to end', async () => {
    const ssoToken = await issuer.ssoToken({ aud: BOT_APP_ID_URI, ver: '1.0', iss: `https://sts.windows.net/${TENANT_ID}/` });
    const { body } = await invoke({ authentication: { id: 'ter-1', connectionName: 'sso', token: ssoToken } });

    expect(body.statusCode).toBe(200);
    expect(JSON.stringify(body.value)).toContain('the downstream API accepted your token');
  });
});

describe('routing', () => {
  it('answers unknown Action.Execute verbs with 400 instead of starting a sign-in', async () => {
    const activity = cardActionActivity();
    activity.value.action = { type: 'Action.Execute', verb: 'deleteEverything', data: { action: 'deleteEverything' } };
    const { body } = await post(activity);

    expect(body.statusCode).toBe(400);
    expect(body.value.message).toContain('deleteEverything');
  });
});

function freePort(): Promise<number> {
  return new Promise((resolve, reject) => {
    const srv = createServer().listen(0, () => {
      const { port } = srv.address() as AddressInfo;
      srv.close(() => resolve(port));
    });
    srv.on('error', reject);
  });
}
