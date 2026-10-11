import { generateKeyPair } from 'jose';
import request from 'supertest';
import { beforeAll, describe, expect, it } from 'vitest';
import { createApp } from '../src/app.js';
import { createEntraJwtVerifier } from '../src/entra-jwt.js';
import { API_CLIENT_ID, BOT_CLIENT_ID, createTestIssuer, TENANT_ID, USER_OID } from './test-issuer.js';

let issuer: Awaited<ReturnType<typeof createTestIssuer>>;
let app: ReturnType<typeof createApp>;

beforeAll(async () => {
  issuer = await createTestIssuer();
  app = createApp(
    createEntraJwtVerifier({
      tenantId: TENANT_ID,
      audiences: [API_CLIENT_ID, `api://${API_CLIENT_ID}`],
      requiredScopes: ['access_as_user'],
      jwks: issuer.jwks,
    }),
  );
});

const get = (token?: string) => {
  const req = request(app).get('/api/me');
  return token ? req.set('Authorization', `Bearer ${token}`) : req;
};

describe('GET /api/me', () => {
  it('accepts a v2 delegated token and reports the user and the calling app (the bot)', async () => {
    const res = await get(await issuer.userToken());
    expect(res.status).toBe(200);
    expect(res.body.user).toMatchObject({ name: 'Adele Vance', username: 'adele@contoso.com', oid: USER_OID });
    expect(res.body.token).toMatchObject({ aud: API_CLIENT_ID, scp: 'access_as_user', callingAppId: BOT_CLIENT_ID });
  });

  it('accepts a v1 token (sts.windows.net issuer, App ID URI audience, upn/appid claims)', async () => {
    const token = await issuer.userToken({
      ver: '1.0',
      iss: `https://sts.windows.net/${TENANT_ID}/`,
      aud: `api://${API_CLIENT_ID}`,
      preferred_username: undefined,
      upn: 'adele@contoso.com',
      azp: undefined,
      appid: BOT_CLIENT_ID,
    });
    const res = await get(token);
    expect(res.status).toBe(200);
    expect(res.body.user.username).toBe('adele@contoso.com');
    expect(res.body.token.callingAppId).toBe(BOT_CLIENT_ID);
  });

  it('rejects a request with no token', async () => {
    const res = await get();
    expect(res.status).toBe(401);
    expect(res.body.error).toBe('missing_token');
  });

  it('rejects a token for a different audience (e.g. the raw Teams SSO token meant for the bot)', async () => {
    const res = await get(await issuer.userToken({ aud: `api://botid-${BOT_CLIENT_ID}` }));
    expect(res.status).toBe(401);
    expect(res.body.message).toMatch(/"aud"/);
  });

  it('rejects a token from another tenant', async () => {
    const other = '99999999-9999-9999-9999-999999999999';
    const res = await get(await issuer.userToken({ iss: `https://login.microsoftonline.com/${other}/v2.0`, tid: other }));
    expect(res.status).toBe(401);
  });

  it('rejects an expired token', async () => {
    const res = await get(await issuer.userToken({}, { expiresIn: Math.floor(Date.now() / 1000) - 3600 }));
    expect(res.status).toBe(401);
    expect(res.body.message).toMatch(/expired/);
  });

  it('rejects a token signed by an unknown key', async () => {
    const { privateKey } = await generateKeyPair('RS256');
    const res = await get(await issuer.userToken({}, { key: privateKey }));
    expect(res.status).toBe(401);
  });

  it('rejects an app-only token (roles, no scp) with 403', async () => {
    const res = await get(await issuer.userToken({ scp: undefined, roles: ['Data.Read.All'] }));
    expect(res.status).toBe(403);
    expect(res.body.error).toBe('insufficient_scope');
  });

  it('rejects a user token without the required scope with 403', async () => {
    const res = await get(await issuer.userToken({ scp: 'User.Read' }));
    expect(res.status).toBe(403);
    expect(res.body.message).toMatch(/access_as_user/);
  });
});
