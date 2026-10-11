import { createLocalJWKSet, exportJWK, generateKeyPair, SignJWT, type CryptoKey, type JWTPayload } from 'jose';

export const TENANT_ID = '11111111-1111-1111-1111-111111111111';
export const API_CLIENT_ID = '22222222-2222-2222-2222-222222222222';
export const BOT_CLIENT_ID = '33333333-3333-3333-3333-333333333333';
export const USER_OID = '44444444-4444-4444-4444-444444444444';

/** A fake Entra tenant: an RSA key, its JWKS, and a token minting helper. */
export async function createTestIssuer() {
  const { publicKey, privateKey } = await generateKeyPair('RS256');
  const jwk = { ...(await exportJWK(publicKey)), kid: 'test-key', alg: 'RS256', use: 'sig' };
  const jwks = createLocalJWKSet({ keys: [jwk] });

  async function sign(claims: JWTPayload, opts: { expiresIn?: string | number; key?: CryptoKey } = {}) {
    return new SignJWT(claims)
      .setProtectedHeader({ alg: 'RS256', kid: 'test-key', typ: 'JWT' })
      .setIssuedAt()
      .setExpirationTime(opts.expiresIn ?? '1h')
      .sign(opts.key ?? privateKey);
  }

  /** A v2 delegated token like the one the bot gets from OBO. */
  function userToken(overrides: JWTPayload = {}, opts?: { expiresIn?: string | number; key?: CryptoKey }) {
    return sign(
      {
        ver: '2.0',
        iss: `https://login.microsoftonline.com/${TENANT_ID}/v2.0`,
        aud: API_CLIENT_ID,
        tid: TENANT_ID,
        oid: USER_OID,
        name: 'Adele Vance',
        preferred_username: 'adele@contoso.com',
        scp: 'access_as_user',
        azp: BOT_CLIENT_ID,
        ...overrides,
      },
      opts,
    );
  }

  return { jwks, sign, userToken };
}
