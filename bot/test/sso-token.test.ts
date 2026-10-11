import { beforeAll, describe, expect, it } from 'vitest';
import { createSsoTokenValidator, TokenValidationError } from '../src/sso/index.js';
import { API_CLIENT_ID, BOT_APP_ID_URI, BOT_CLIENT_ID, createTestIssuer, TENANT_ID, USER_OID } from './helpers.js';

let issuer: Awaited<ReturnType<typeof createTestIssuer>>;
let validate: ReturnType<typeof createSsoTokenValidator>;

beforeAll(async () => {
  issuer = await createTestIssuer();
  validate = createSsoTokenValidator({
    tenantId: TENANT_ID,
    clientId: BOT_CLIENT_ID,
    botAppIdUri: BOT_APP_ID_URI,
    botSsoScope: 'access_as_user',
    jwks: issuer.jwks,
  });
});

describe('SSO token validation', () => {
  it('accepts the token Teams obtains for the bot (v2: aud = bot client ID)', async () => {
    const claims = await validate(await issuer.ssoToken(), USER_OID);
    expect(claims).toMatchObject({ aud: BOT_CLIENT_ID, scp: 'access_as_user', oid: USER_OID });
  });

  it('accepts a v1 SSO token (aud = api://botid-...)', async () => {
    const token = await issuer.ssoToken({ ver: '1.0', iss: `https://sts.windows.net/${TENANT_ID}/`, aud: BOT_APP_ID_URI });
    await expect(validate(token, USER_OID)).resolves.toMatchObject({ aud: BOT_APP_ID_URI });
  });

  it('rejects a token for a different user than the one who clicked', async () => {
    await expect(validate(await issuer.ssoToken(), '55555555-5555-5555-5555-555555555555')).rejects.toThrow(
      /different user/,
    );
  });

  it('rejects a token that was meant for another API', async () => {
    await expect(validate(await issuer.ssoToken({ aud: API_CLIENT_ID }), USER_OID)).rejects.toBeInstanceOf(
      TokenValidationError,
    );
  });

  it('rejects a token without the access_as_user scope', async () => {
    await expect(validate(await issuer.ssoToken({ scp: 'User.Read' }), USER_OID)).rejects.toThrow(/access_as_user/);
  });
});
