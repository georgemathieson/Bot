export type TokenStrategyName = 'obo' | 'token-service';

export interface BotConfig {
  /** Bot's Entra app registration (client) ID. Also the Azure Bot's Microsoft App ID. */
  clientId: string;
  clientSecret: string;
  tenantId: string;
  /**
   * Application ID URI the bot exposes for Teams SSO. Must match:
   *  - Entra app reg > Expose an API > Application ID URI
   *  - manifest.json > webApplicationInfo.resource
   *  - Azure Bot OAuth connection > Token Exchange URL
   */
  botAppIdUri: string;
  /** Delegated scope the bot exposes for Teams SSO (Expose an API > Scopes). */
  botSsoScope: string;
  /** Name of the OAuth connection on the Azure Bot resource. */
  oauthConnectionName: string;
  downstream: {
    url: string;
    /** Scopes requested for the downstream API, e.g. api://<api-client-id>/access_as_user */
    scopes: string[];
  };
  /**
   * How the Teams SSO token is turned into a downstream token:
   *  - obo: the bot calls Entra's OBO endpoint itself with MSAL (default)
   *  - token-service: the Bot Framework token service does the OBO exchange using the OAuth connection
   */
  tokenStrategy: TokenStrategyName;
  /** POC diagnostics: show decoded (never raw) token claims and error details on cards. */
  showDiagnostics: boolean;
  port: number;
}

type Env = Record<string, string | undefined>;

export function loadConfig(env: Env = process.env): BotConfig {
  const get = (name: string) => env[name]?.trim() || undefined;
  const required = (name: string) => {
    const value = get(name);
    if (!value) throw new Error(`Missing required environment variable ${name} (see bot/.env.example)`);
    return value;
  };

  const clientId = required('CLIENT_ID');
  const tokenStrategy = (get('TOKEN_STRATEGY') ?? 'obo') as TokenStrategyName;
  if (tokenStrategy !== 'obo' && tokenStrategy !== 'token-service') {
    throw new Error(`TOKEN_STRATEGY must be "obo" or "token-service", got "${tokenStrategy}"`);
  }

  return {
    clientId,
    clientSecret: required('CLIENT_SECRET'),
    tenantId: required('TENANT_ID'),
    botAppIdUri: get('BOT_APP_ID_URI') ?? `api://botid-${clientId}`,
    botSsoScope: get('BOT_SSO_SCOPE') ?? 'access_as_user',
    oauthConnectionName: get('OAUTH_CONNECTION_NAME') ?? 'sso',
    downstream: {
      url: required('DOWNSTREAM_API_URL'),
      scopes: required('DOWNSTREAM_API_SCOPES').split(/[\s,]+/).filter(Boolean),
    },
    tokenStrategy,
    showDiagnostics: (get('SHOW_DIAGNOSTICS') ?? 'true').toLowerCase() !== 'false',
    port: Number(get('PORT') ?? 3978),
  };
}
