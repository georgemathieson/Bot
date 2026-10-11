import express, { type NextFunction, type Request, type Response } from 'express';
import { TokenValidationError, type EntraAccessTokenClaims, type EntraJwtVerifier } from './entra-jwt.js';

declare module 'express-serve-static-core' {
  interface Request {
    user?: EntraAccessTokenClaims;
  }
}

/** Express middleware: require a valid Entra user token in the Authorization header. */
export function requireEntraUser(verify: EntraJwtVerifier) {
  return async (req: Request, res: Response, next: NextFunction) => {
    const header = req.headers.authorization ?? '';
    const match = /^Bearer\s+(.+)$/i.exec(header);
    if (!match?.[1]) {
      res.status(401).set('WWW-Authenticate', 'Bearer').json({ error: 'missing_token' });
      return;
    }
    try {
      req.user = await verify(match[1]);
      next();
    } catch (err) {
      if (err instanceof TokenValidationError) {
        res
          .status(err.status)
          .set('WWW-Authenticate', `Bearer error="${err.status === 401 ? 'invalid_token' : 'insufficient_scope'}"`)
          .json({ error: err.status === 401 ? 'invalid_token' : 'insufficient_scope', message: err.message });
        return;
      }
      next(err);
    }
  };
}

export function createApp(verify: EntraJwtVerifier) {
  const app = express();
  app.disable('x-powered-by');

  app.get('/healthz', (_req, res) => {
    res.json({ status: 'ok' });
  });

  /**
   * Returns who the API thinks is calling. In the POC this proves the token
   * the bot obtained via OBO is a real delegated token for the Teams user:
   * - oid / name / preferred_username identify the user
   * - azp (v2) / appid (v1) is the bot's client ID, i.e. the bot called us *on behalf of* that user
   */
  app.get('/api/me', requireEntraUser(verify), (req, res) => {
    const c = req.user!;
    res.json({
      message: `Hello ${c.name ?? c.preferred_username ?? c.upn ?? 'unknown user'}, the downstream API accepted your token.`,
      user: {
        name: c.name,
        username: c.preferred_username ?? c.upn,
        oid: c.oid,
        tid: c.tid,
      },
      token: {
        ver: c.ver,
        aud: c.aud,
        scp: c.scp,
        callingAppId: c.azp ?? c.appid,
        amr: c.amr,
        expiresAt: c.exp ? new Date(c.exp * 1000).toISOString() : undefined,
      },
    });
  });

  return app;
}
