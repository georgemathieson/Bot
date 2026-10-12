#!/usr/bin/env bash
# Creates the identities this POC needs, configured for popup-free Teams SSO + OBO with no secret in Azure:
#
#   1. Downstream API         - app registration, exposes api://<api-id>/access_as_user, v2 tokens
#   2. Bot managed identity   - user-assigned managed identity. Its client ID is the Azure Bot's Microsoft App ID
#                               (Azure Bot "Type of App" = User-Assigned Managed Identity)
#   3. SSO app registration   - exposes api://botid-<sso-app-id>/access_as_user, v2 tokens,
#                               Teams desktop/mobile + web clients pre-authorized,
#                               delegated permission on the downstream API scope (+ openid/profile/offline_access),
#                               token service redirect URI, admin consent,
#                               federated identity credential that trusts the managed identity (no secret)
#
# With LOCAL_DEV=true (the default) it also adds a client secret to the SSO app registration, so that you can run
# the bot on your computer, where there is no managed identity, and stores the settings with `dotnet user-secrets`.
# Set LOCAL_DEV=false to make no secret at all.
#
# Requires: Azure CLI logged in (`az login`) as someone who can create apps, grant admin consent and create a
# managed identity in the subscription, and the .NET SDK.
# Review before running - it creates new app registrations every time it runs.
set -euo pipefail

API_APP_NAME="${API_APP_NAME:-poc-downstream-api}"
SSO_APP_NAME="${SSO_APP_NAME:-poc-teams-sso-bot}"
RESOURCE_GROUP="${RESOURCE_GROUP:-rg-teams-sso-poc}"
LOCATION="${LOCATION:-westeurope}"
IDENTITY_NAME="${IDENTITY_NAME:-id-teams-sso-bot}"
LOCAL_DEV="${LOCAL_DEV:-true}"
# Use https://europe.token.botframework.com/... etc. if your Azure Bot uses a regional token service.
TOKEN_SERVICE_REDIRECT="${TOKEN_SERVICE_REDIRECT:-https://token.botframework.com/.auth/web/redirect}"
# Audience of the managed identity token for the federated credential (global Azure).
TOKEN_EXCHANGE_AUDIENCE="${TOKEN_EXCHANGE_AUDIENCE:-api://AzureADTokenExchange}"

TEAMS_DESKTOP_MOBILE=1fec8e78-bce4-4aaf-ab1b-5451cc387264
TEAMS_WEB=5e3ce6c0-2b1f-4285-8d4b-75ee78787346
MS_GRAPH=00000003-0000-0000-c000-000000000000
GRAPH_OPENID=37f7f235-527c-4136-accd-4a02d197296e
GRAPH_PROFILE=14dad69e-099b-42c9-810b-d002981feec1
GRAPH_OFFLINE_ACCESS=7427e0e9-2fba-42fe-b0c0-848c9e6a8182

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

command -v az >/dev/null || { echo "Azure CLI (az) is required: https://aka.ms/azcli" >&2; exit 1; }
command -v dotnet >/dev/null || { echo ".NET SDK (dotnet) is required: https://dot.net" >&2; exit 1; }

uuid() { if command -v uuidgen >/dev/null; then uuidgen | tr '[:upper:]' '[:lower:]'; else cat /proc/sys/kernel/random/uuid; fi; }

patch_app() { # <object-id> <json-file>
  az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications/$1" \
    --headers "Content-Type=application/json" --body "@$2" --output none
}

scope_json() { # <id> <display name> <description>
  cat <<EOF
{ "id": "$1", "value": "access_as_user", "type": "User", "isEnabled": true,
  "adminConsentDisplayName": "$2", "adminConsentDescription": "$3",
  "userConsentDisplayName": "$2", "userConsentDescription": "$3" }
EOF
}

TENANT_ID="$(az account show --query tenantId -o tsv)"
echo "Tenant: $TENANT_ID"

# --- 1. Downstream API -------------------------------------------------------------------------
echo "Creating app registration '$API_APP_NAME' (downstream API)..."
API_APP_ID="$(az ad app create --display-name "$API_APP_NAME" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
API_OBJ_ID="$(az ad app show --id "$API_APP_ID" --query id -o tsv)"
API_SCOPE_ID="$(uuid)"
cat >"$TMP/api.json" <<EOF
{
  "identifierUris": ["api://$API_APP_ID"],
  "api": {
    "requestedAccessTokenVersion": 2,
    "oauth2PermissionScopes": [
      $(scope_json "$API_SCOPE_ID" "Access the downstream API as the signed-in user" "Allows the app to call the downstream API on behalf of the signed-in user.")
    ]
  }
}
EOF
patch_app "$API_OBJ_ID" "$TMP/api.json"
az ad sp create --id "$API_APP_ID" --output none

# --- 2. Bot managed identity ------------------------------------------------------------------
echo "Creating user-assigned managed identity '$IDENTITY_NAME' in resource group '$RESOURCE_GROUP'..."
az group create --name "$RESOURCE_GROUP" --location "$LOCATION" --output none
read -r MI_CLIENT_ID MI_PRINCIPAL_ID MI_RESOURCE_ID <<<"$(az identity create --resource-group "$RESOURCE_GROUP" \
  --name "$IDENTITY_NAME" --query "[clientId, principalId, id]" -o tsv | tr '\n' ' ')"

# --- 3. SSO app registration -------------------------------------------------------------------
echo "Creating app registration '$SSO_APP_NAME' (SSO app: Teams SSO audience and OBO caller)..."
SSO_APP_ID="$(az ad app create --display-name "$SSO_APP_NAME" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
SSO_OBJ_ID="$(az ad app show --id "$SSO_APP_ID" --query id -o tsv)"
SSO_SCOPE_ID="$(uuid)"
SSO_SCOPE="$(scope_json "$SSO_SCOPE_ID" "Teams can access the bot as the signed-in user" "Allows Teams to call the bot's API on behalf of the signed-in user.")"

cat >"$TMP/sso-1.json" <<EOF
{
  "identifierUris": ["api://botid-$SSO_APP_ID"],
  "api": {
    "requestedAccessTokenVersion": 2,
    "oauth2PermissionScopes": [ $SSO_SCOPE ]
  },
  "web": { "redirectUris": ["$TOKEN_SERVICE_REDIRECT"] },
  "requiredResourceAccess": [
    { "resourceAppId": "$API_APP_ID", "resourceAccess": [ { "id": "$API_SCOPE_ID", "type": "Scope" } ] },
    { "resourceAppId": "$MS_GRAPH", "resourceAccess": [
      { "id": "$GRAPH_OPENID", "type": "Scope" },
      { "id": "$GRAPH_PROFILE", "type": "Scope" },
      { "id": "$GRAPH_OFFLINE_ACCESS", "type": "Scope" }
    ] }
  ]
}
EOF
patch_app "$SSO_OBJ_ID" "$TMP/sso-1.json"

# Pre-authorizing needs the scope to exist already, hence a second update.
cat >"$TMP/sso-2.json" <<EOF
{
  "api": {
    "requestedAccessTokenVersion": 2,
    "oauth2PermissionScopes": [ $SSO_SCOPE ],
    "preAuthorizedApplications": [
      { "appId": "$TEAMS_DESKTOP_MOBILE", "delegatedPermissionIds": ["$SSO_SCOPE_ID"] },
      { "appId": "$TEAMS_WEB", "delegatedPermissionIds": ["$SSO_SCOPE_ID"] }
    ]
  }
}
EOF
patch_app "$SSO_OBJ_ID" "$TMP/sso-2.json"
az ad sp create --id "$SSO_APP_ID" --output none

# The SSO app trusts the managed identity: the subject is the managed identity's object (principal) ID.
cat >"$TMP/fic.json" <<EOF
{
  "name": "bot-managed-identity",
  "issuer": "https://login.microsoftonline.com/$TENANT_ID/v2.0",
  "subject": "$MI_PRINCIPAL_ID",
  "description": "The bot's user-assigned managed identity ($IDENTITY_NAME) is the credential of this app",
  "audiences": ["$TOKEN_EXCHANGE_AUDIENCE"]
}
EOF
az ad app federated-credential create --id "$SSO_OBJ_ID" --parameters "@$TMP/fic.json" --output none

# --- 4. Admin consent (the "no popup" part) ----------------------------------------------------
echo "Granting admin consent for the SSO app's permissions (waiting for replication)..."
sleep 30
if ! az ad app permission admin-consent --id "$SSO_APP_ID"; then
  echo "WARNING: admin consent failed. Grant it in the portal: App registrations > $SSO_APP_NAME >" >&2
  echo "         API permissions > Grant admin consent for <tenant>." >&2
fi

# --- 5. Settings -------------------------------------------------------------------------------
cat <<EOF

Settings for the bot in Azure (App Service or Container Apps application settings). None of them is a secret.
The bot's identity is the managed identity (the Agents SDK service connection); the SSO app uses it as its credential:
  Connections__ServiceConnection__Settings__AuthType=UserManagedIdentity
  Connections__ServiceConnection__Settings__ClientId=$MI_CLIENT_ID
  Connections__ServiceConnection__Settings__TenantId=$TENANT_ID
  SsoApp__ClientId=$SSO_APP_ID
  DownstreamApi__Scopes__0=api://$API_APP_ID/access_as_user
  DownstreamApi__Url=https://<downstream-api-host>/api/me
EOF

secret() { dotnet user-secrets set "$2" "$3" --project "$ROOT/src/$1" >/dev/null; }
secret DownstreamApi "AzureAd:TenantId" "$TENANT_ID"
secret DownstreamApi "AzureAd:ClientId" "$API_APP_ID"

if [ "$LOCAL_DEV" = "true" ]; then
  # On your computer there is no managed identity: the SSO app registration is also the bot (single-app setup).
  echo "LOCAL_DEV=true: adding a client secret to '$SSO_APP_NAME' and storing local settings with dotnet user-secrets..."
  SSO_SECRET="$(az ad app credential reset --id "$SSO_APP_ID" --display-name "teams-sso-poc-local" --years 1 --query password -o tsv)"
  secret Bot "Connections:ServiceConnection:Settings:AuthType" "ClientSecret"
  secret Bot "Connections:ServiceConnection:Settings:ClientId" "$SSO_APP_ID"
  secret Bot "Connections:ServiceConnection:Settings:TenantId" "$TENANT_ID"
  secret Bot "Connections:ServiceConnection:Settings:ClientSecret" "$SSO_SECRET"
  secret Bot "DownstreamApi:Scopes:0" "api://$API_APP_ID/access_as_user"
fi

cat <<EOF

Done. Next (docs/azure-setup.md):
  - Azure Bot, type User-Assigned Managed Identity:
      az bot create -g $RESOURCE_GROUP -n <unique-bot-name> --app-type UserAssignedMSI \\
        --appid $MI_CLIENT_ID --tenant-id $TENANT_ID --msi-resource-id $MI_RESOURCE_ID \\
        --endpoint https://<bot-host>/api/messages
  - Assign the managed identity to the App Service or Container App that runs the bot:
      $MI_RESOURCE_ID
  - Add an OAuth connection named "sso" to the Azure Bot (docs/azure-setup.md, step 5):
      Client id          $SSO_APP_ID
      Token Exchange URL api://botid-$SSO_APP_ID
      Tenant ID          $TENANT_ID
      Scopes             api://$API_APP_ID/access_as_user
  - Package the Teams app:
      dotnet run scripts/package-app.cs -- $MI_CLIENT_ID $SSO_APP_ID
EOF
if [ "$LOCAL_DEV" = "true" ]; then
  cat <<EOF
  - To run the bot on your computer: a second Azure Bot, type Single Tenant, Microsoft App ID $SSO_APP_ID,
    with its own "sso" OAuth connection, and a Teams app package for it:
      dotnet run scripts/package-app.cs -- $SSO_APP_ID
EOF
fi
