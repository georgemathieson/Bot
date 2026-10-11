import type { JWTVerifyGetKey } from 'jose';
import { createEntraJwtVerifier, TokenValidationError, type EntraAccessTokenClaims } from './entra-jwt.js';

export type SsoTokenValidator = (token: string, expectedUserOid?: string) => Promise<EntraAccessTokenClaims>;

/**
 * Validates the token Teams hands back after a silent SSO handshake.
 *
 * This is a *real* Entra access token: issued to the Teams client (azp = Teams' client ID)
 * for the bot's own API (aud = bot client ID or api://botid-...), with scp = access_as_user.
 * It is the "assertion" for the on-behalf-of exchange.
 *
 * Beyond signature/issuer/audience/scope, we also check the token belongs to the user who
 * clicked the card (oid === activity.from.aadObjectId), so a token can't be replayed on
 * someone else's activity.
 */
export function createSsoTokenValidator(options: {
  tenantId: string;
  clientId: string;
  botAppIdUri: string;
  botSsoScope: string;
  authorityHost?: string;
  jwks?: JWTVerifyGetKey;
}): SsoTokenValidator {
  const verify = createEntraJwtVerifier({
    tenantId: options.tenantId,
    audiences: [options.clientId, options.botAppIdUri],
    requiredScopes: [options.botSsoScope],
    authorityHost: options.authorityHost,
    jwks: options.jwks,
  });

  return async (token, expectedUserOid) => {
    const claims = await verify(token);
    if (expectedUserOid && claims.oid !== expectedUserOid) {
      throw new TokenValidationError('SSO token was issued to a different user than the one who sent the activity', 401);
    }
    return claims;
  };
}
