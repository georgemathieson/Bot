#!/usr/bin/env bash
# Creates the two Entra ID app registrations this POC needs, configured for popup-free Teams SSO + OBO:
#
#   1. Downstream API  - exposes api://<api-id>/access_as_user, v2 tokens
#   2. Bot             - exposes api://botid-<bot-id>/access_as_user, v2 tokens,
#                        Teams desktop/mobile + web clients pre-authorized,
#                        delegated permission on the downstream API scope (+ openid/profile/offline_access),
#                        token service redirect URI, client secret, admin consent
#
# Then writes bot/.env and downstream-api/.env (if they don't exist yet).
# Requires: Azure CLI logged in (`az login`) as someone who can create apps and grant admin consent.
# Review before running - it creates new app registrations every time it runs.
set -euo pipefail

API_APP_NAME="${API_APP_NAME:-poc-downstream-api}"
BOT_APP_NAME="${BOT_APP_NAME:-poc-teams-sso-bot}"
# Use https://europe.token.botframework.com/... etc. if your Azure Bot uses a regional token service.
TOKEN_SERVICE_REDIRECT="${TOKEN_SERVICE_REDIRECT:-https://token.botframework.com/.auth/web/redirect}"

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

# --- 2. Bot ------------------------------------------------------------------------------------
echo "Creating app registration '$BOT_APP_NAME' (bot)..."
BOT_APP_ID="$(az ad app create --display-name "$BOT_APP_NAME" --sign-in-audience AzureADMyOrg --query appId -o tsv)"
BOT_OBJ_ID="$(az ad app show --id "$BOT_APP_ID" --query id -o tsv)"
BOT_SCOPE_ID="$(uuid)"
BOT_SCOPE="$(scope_json "$BOT_SCOPE_ID" "Teams can access the bot as the signed-in user" "Allows Teams to call the bot's API on behalf of the signed-in user.")"

cat >"$TMP/bot-1.json" <<EOF
{
  "identifierUris": ["api://botid-$BOT_APP_ID"],
  "api": {
    "requestedAccessTokenVersion": 2,
    "oauth2PermissionScopes": [ $BOT_SCOPE ]
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
patch_app "$BOT_OBJ_ID" "$TMP/bot-1.json"

# Pre-authorizing needs the scope to exist already, hence a second update.
cat >"$TMP/bot-2.json" <<EOF
{
  "api": {
    "requestedAccessTokenVersion": 2,
    "oauth2PermissionScopes": [ $BOT_SCOPE ],
    "preAuthorizedApplications": [
      { "appId": "$TEAMS_DESKTOP_MOBILE", "delegatedPermissionIds": ["$BOT_SCOPE_ID"] },
      { "appId": "$TEAMS_WEB", "delegatedPermissionIds": ["$BOT_SCOPE_ID"] }
    ]
  }
}
EOF
patch_app "$BOT_OBJ_ID" "$TMP/bot-2.json"
az ad sp create --id "$BOT_APP_ID" --output none
BOT_SECRET="$(az ad app credential reset --id "$BOT_APP_ID" --display-name "teams-sso-poc" --years 1 --query password -o tsv)"

# --- 3. Admin consent (the "no popup" part) ----------------------------------------------------
echo "Granting admin consent for the bot's permissions (waiting for replication)..."
sleep 30
if ! az ad app permission admin-consent --id "$BOT_APP_ID"; then
  echo "WARNING: admin consent failed. Grant it in the portal: App registrations > $BOT_APP_NAME >" >&2
  echo "         API permissions > Grant admin consent for <tenant>." >&2
fi

# --- 4. .env files -----------------------------------------------------------------------------
write_env() { # <file> <content>
  if [[ -e "$1" ]]; then
    echo "NOT overwriting existing $1 - merge these values yourself:"
    echo "$2" | sed 's/^/    /'
  else
    printf '%s\n' "$2" >"$1"
    echo "Wrote $1"
  fi
}

write_env "$ROOT/downstream-api/.env" "TENANT_ID=$TENANT_ID
API_CLIENT_ID=$API_APP_ID
PORT=5000"

write_env "$ROOT/bot/.env" "CLIENT_ID=$BOT_APP_ID
CLIENT_SECRET=$BOT_SECRET
TENANT_ID=$TENANT_ID
OAUTH_CONNECTION_NAME=sso
DOWNSTREAM_API_URL=http://localhost:5000/api/me
DOWNSTREAM_API_SCOPES=api://$API_APP_ID/access_as_user
TOKEN_STRATEGY=obo
SHOW_DIAGNOSTICS=true
PORT=3978"

cat <<EOF

Done. Next (docs/azure-setup.md, steps 3-4):
  - Create an Azure Bot (single tenant) with Microsoft App ID $BOT_APP_ID
    and messaging endpoint https://<your-tunnel>/api/messages, and enable the Teams channel.
  - Add an OAuth connection named "sso" (Azure Active Directory v2):
      Client id          $BOT_APP_ID
      Client secret      (the CLIENT_SECRET in bot/.env)
      Token Exchange URL api://botid-$BOT_APP_ID
      Tenant ID          $TENANT_ID
      Scopes             api://$API_APP_ID/access_as_user
EOF
