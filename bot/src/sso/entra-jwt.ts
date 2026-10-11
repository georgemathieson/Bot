// Same verifier as downstream-api/src/entra-jwt.ts. It is duplicated on purpose so
// each app stays self-contained and can be lifted out of this POC on its own.

import { createRemoteJWKSet, errors, jwtVerify, type JWTPayload, type JWTVerifyGetKey } from 'jose';

/** Claims we care about on an Entra ID (Azure AD) access token. */
export interface EntraAccessTokenClaims extends JWTPayload {
  /** Token version: "1.0" or "2.0" (set by accessTokenAcceptedVersion / requestedAccessTokenVersion). */
  ver?: string;
  tid?: string;
  /** Object ID of the signed-in user. */
  oid?: string;
  name?: string;
  /** v2 tokens */
  preferred_username?: string;
  /** v1 tokens */
  upn?: string;
  /** Space-separated delegated scopes. Present only on user (delegated) tokens. */
  scp?: string;
  /** App roles. App-only tokens carry roles and no scp. */
  roles?: string[];
  /** Client that requested the token (v2). For an OBO token this is the middle tier (the bot). */
  azp?: string;
  /** Client that requested the token (v1). */
  appid?: string;
  amr?: string[];
}

export interface EntraJwtVerifierOptions {
  tenantId: string;
  /** Accepted "aud" values. Include the client ID (v2 tokens) and the Application ID URI (v1 tokens). */
  audiences: string[];
  /** Delegated scopes that must all be present in "scp". */
  requiredScopes: string[];
  /** Defaults to https://login.microsoftonline.com (change for sovereign clouds). */
  authorityHost?: string;
  /** Override key resolution (tests). Defaults to the tenant's published JWKS. */
  jwks?: JWTVerifyGetKey;
  /** Seconds of clock skew to tolerate. */
  clockToleranceSeconds?: number;
}

export class TokenValidationError extends Error {
  constructor(
    message: string,
    /** 401 = not a valid token for us, 403 = valid token but not allowed. */
    readonly status: 401 | 403,
  ) {
    super(message);
    this.name = 'TokenValidationError';
  }
}

export type EntraJwtVerifier = (token: string) => Promise<EntraAccessTokenClaims>;

/**
 * Validates an Entra ID access token the way any protected API should:
 * signature (tenant JWKS), issuer (v1 or v2 endpoint of *our* tenant), audience,
 * expiry, and that it is a delegated user token carrying the required scope(s).
 */
export function createEntraJwtVerifier(options: EntraJwtVerifierOptions): EntraJwtVerifier {
  const authorityHost = (options.authorityHost ?? 'https://login.microsoftonline.com').replace(/\/$/, '');
  const jwks =
    options.jwks ?? createRemoteJWKSet(new URL(`${authorityHost}/${options.tenantId}/discovery/v2.0/keys`));
  const issuers = [
    `${authorityHost}/${options.tenantId}/v2.0`, // v2 access tokens
    `https://sts.windows.net/${options.tenantId}/`, // v1 access tokens
  ];

  return async (token: string) => {
    let claims: EntraAccessTokenClaims;
    try {
      ({ payload: claims } = await jwtVerify<EntraAccessTokenClaims>(token, jwks, {
        issuer: issuers,
        audience: options.audiences,
        algorithms: ['RS256'],
        clockTolerance: options.clockToleranceSeconds ?? 60,
      }));
    } catch (err) {
      throw new TokenValidationError(describeJoseError(err), 401);
    }

    if (claims.tid !== options.tenantId) {
      throw new TokenValidationError(`Token is for tenant ${claims.tid}, expected ${options.tenantId}`, 401);
    }
    if (!claims.scp) {
      // App-only (client credentials) tokens have "roles" but no "scp". We only accept user tokens.
      throw new TokenValidationError('Token has no delegated scopes (scp) - an app-only token is not accepted', 403);
    }
    const granted = new Set(claims.scp.split(' '));
    const missing = options.requiredScopes.filter((s) => !granted.has(s));
    if (missing.length > 0) {
      throw new TokenValidationError(`Token is missing required scope(s): ${missing.join(', ')}`, 403);
    }
    return claims;
  };
}

function describeJoseError(err: unknown): string {
  // JWTExpired extends JWTClaimValidationFailed, so check it first.
  if (err instanceof errors.JWTExpired) return 'Token has expired';
  if (err instanceof errors.JWTClaimValidationFailed) {
    return `Token claim "${err.claim}" is invalid (${err.reason}): ${err.message}`;
  }
  if (err instanceof errors.JWSSignatureVerificationFailed) return 'Token signature is invalid';
  if (err instanceof errors.JWKSNoMatchingKey) return 'Token was not signed by a key from this tenant';
  if (err instanceof errors.JOSEError) return `Token is invalid: ${err.message}`;
  return `Token could not be validated: ${err instanceof Error ? err.message : String(err)}`;
}
