import { createApp } from './app.js';
import { createEntraJwtVerifier } from './entra-jwt.js';

function required(name: string): string {
  const value = process.env[name]?.trim();
  if (!value) throw new Error(`Missing required environment variable ${name} (see downstream-api/.env.example)`);
  return value;
}

const tenantId = required('TENANT_ID');
const clientId = required('API_CLIENT_ID');
const appIdUri = process.env.API_APP_ID_URI?.trim() || `api://${clientId}`;
const requiredScope = process.env.REQUIRED_SCOPE?.trim() || 'access_as_user';
const port = Number(process.env.PORT ?? 5000);

const verify = createEntraJwtVerifier({
  tenantId,
  audiences: [clientId, appIdUri],
  requiredScopes: [requiredScope],
});

createApp(verify).listen(port, () => {
  console.log(`downstream-api listening on http://localhost:${port} (aud: ${clientId} | ${appIdUri}, scope: ${requiredScope})`);
});
