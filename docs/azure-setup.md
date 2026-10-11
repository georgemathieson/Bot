# Azure / Entra ID setup

You need two app registrations, one Azure Bot resource with an OAuth connection, and a Teams app
package. Where this says *bot ID*, it means the bot app registration's **Application (client) ID**.

| Value | Must be identical in |
| --- | --- |
| `api://botid-<bot ID>` | Bot app reg → Expose an API → Application ID URI · manifest `webApplicationInfo.resource` · OAuth connection **Token Exchange URL** · `Sso:BotAppIdUri` (default) |
| `<bot ID>` | Bot app reg client ID · Azure Bot **Microsoft App ID** · manifest `bots[0].botId` and `webApplicationInfo.id` · `AzureAd:ClientId` (bot) |
| `api://<API ID>/access_as_user` | Downstream API app reg → Expose an API scope · bot app reg → API permissions · OAuth connection **Scopes** · `DownstreamApi:Scopes` (bot) |

**Fast path:** `az login`, then `scripts/setup-entra.sh` does steps 1–2 (including admin consent) and
stores both projects' settings with `dotnet user-secrets`. Then do steps 3–6 below. The script wasn't run against a live tenant while
this POC was built (it was dry-run against a stubbed `az`), so read it first.

---

## 1. Downstream API app registration

This is the "other service". In your real setup it already exists; check that it matches.

Entra admin center → **App registrations** → **New registration**

- Name: `poc-downstream-api`, Supported account types: **this organizational directory only**
- **Expose an API**
  - Application ID URI: `api://<API ID>` (the default)
  - **Add a scope**: `access_as_user`, Who can consent: *Admins and users*, State: Enabled
- **Manifest**: set `"requestedAccessTokenVersion": 2` (shown as `accessTokenAcceptedVersion` in the
  legacy manifest editor). Optional, because the sample API accepts v1 and v2 tokens.

Settings for `src/DownstreamApi` (user-secrets or appsettings): `AzureAd:TenantId`, `AzureAd:ClientId` = API ID.

```bash
dotnet user-secrets set "AzureAd:TenantId" "<tenant ID>" --project src/DownstreamApi
dotnet user-secrets set "AzureAd:ClientId" "<API ID>" --project src/DownstreamApi
```

## 2. Bot app registration

**New registration** → `poc-teams-sso-bot`, **single tenant** (multi-tenant bots are deprecated).

1. **Certificates & secrets** → New client secret → store it as `AzureAd:ClientCredentials:0:ClientSecret` (below).
2. **Expose an API**
   - Application ID URI: **`api://botid-<bot ID>`**. Change it from the default `api://<bot ID>`.
   - **Add a scope**: `access_as_user`, *Admins and users*, Enabled.
   - **Add a client application** for each of these, ticking the `access_as_user` scope:
     - `1fec8e78-bce4-4aaf-ab1b-5451cc387264` (Teams desktop/mobile)
     - `5e3ce6c0-2b1f-4285-8d4b-75ee78787346` (Teams web)

     This pre-authorization is what lets Teams get a token for your bot **without a consent prompt**.
3. **Manifest**: `"requestedAccessTokenVersion": 2`.
4. **Authentication** → Add a platform → **Web** → Redirect URI
   `https://token.botframework.com/.auth/web/redirect`. The token service uses it for the
   sign-in fallback. Use the regional host, e.g. `europe.token.botframework.com`, if your bot uses one.
5. **API permissions** → Add a permission → *APIs my organization uses* → `poc-downstream-api` →
   Delegated → `access_as_user`. Optionally add Microsoft Graph delegated `openid`, `profile` and
   `offline_access`, so the fallback sign-in is also covered by admin consent.
6. **Grant admin consent for \<tenant\>**. This is the OBO consent: it lets the bot exchange the user's
   token for one to the downstream API. Without it, users get a one-time Sign in prompt, or OBO fails
   with `AADSTS65001`.

Settings for `src/Bot`. Keep the secret out of `appsettings.json`:

```bash
dotnet user-secrets set "AzureAd:TenantId" "<tenant ID>" --project src/Bot
dotnet user-secrets set "AzureAd:ClientId" "<bot ID>" --project src/Bot
dotnet user-secrets set "AzureAd:ClientCredentials:0:ClientSecret" "<secret>" --project src/Bot
dotnet user-secrets set "DownstreamApi:Scopes:0" "api://<API ID>/access_as_user" --project src/Bot
```

## 3. Azure Bot resource

Azure portal → **Create a resource** → **Azure Bot**

- Type of App: **Single Tenant**, Creation type: **Use existing app registration**, App ID = bot ID,
  App tenant ID = your tenant
- **Configuration** → Messaging endpoint: `https://<public host>/api/messages`. For local dev, use a
  tunnel to port 3978: `devtunnel host -p 3978 --allow-anonymous` or `ngrok http 3978`.
- **Channels** → add **Microsoft Teams**

CLI equivalent:

```bash
az group create -n rg-teams-sso-poc -l westeurope
az bot create -g rg-teams-sso-poc -n <unique-bot-name> --app-type SingleTenant \
  --appid <bot ID> --tenant-id <tenant ID> --endpoint https://<public host>/api/messages
az bot msteams create -g rg-teams-sso-poc -n <unique-bot-name>
```

## 4. OAuth connection on the Azure Bot

Azure Bot → **Configuration** → **Add OAuth Connection Settings**

| Field | Value |
| --- | --- |
| Name | `sso` (= `Sso:OAuthConnectionName`) |
| Service Provider | **Azure Active Directory v2** |
| Client id | bot ID |
| Client secret | the bot's secret |
| Token Exchange URL | **`api://botid-<bot ID>`** (without it, Teams can't do SSO and goes straight to the Sign in button) |
| Tenant ID | your tenant ID |
| Scopes | `api://<API ID>/access_as_user` |

Save, then select **Test connection**. You should sign in and get a token whose `aud` is the downstream API.

The bot uses this connection to build the loginRequest (sign-in link + `tokenExchangeResource`) and
for the fallback sign-in. With `Sso:TokenStrategy` = `TokenService`, it also does the OBO exchange.

CLI equivalent (check the parameter names with `az bot authsetting list-providers --provider-name Aadv2`):

```bash
az bot authsetting create -g rg-teams-sso-poc -n <unique-bot-name> -c sso --service Aadv2 \
  --client-id <bot ID> --client-secret '<secret>' \
  --provider-scope-string "api://<API ID>/access_as_user" \
  --parameters tenantId=<tenant ID> tokenExchangeUrl=api://botid-<bot ID>
```

## 5. Run locally

```bash
dotnet run --project src/DownstreamApi   # :5000
dotnet run --project src/Bot             # :3978, the tunnel from step 3 must point here
```

## 6. Teams app package

```bash
dotnet run scripts/package-app.cs -- <bot ID>   # -> appPackage/build/appPackage.zip
```

Teams → **Apps** → **Manage your apps** → **Upload an app** → *Upload a custom app*. Your tenant must allow
custom app upload. Add the app for yourself (personal scope), then send it any message, and select
**Call downstream API** on the card.

The manifest's `webApplicationInfo` is generated from the bot ID (and an optional App ID URI argument):

```json
"webApplicationInfo": { "id": "<bot ID>", "resource": "api://botid-<bot ID>" }
```

## Verifying it's silent

1. First click: you should see the card update with the result and no dialog. The bot log shows no
   "falling back to sign-in" line.
2. If you see a **consent dialog** ("View and accept"), admin consent (step 2.6) or pre-authorization
   (step 2.2) is missing.
3. If you see a **Sign in** button in the card footer, see [troubleshooting](troubleshooting.md). Either
   the SSO handshake isn't configured, or OBO needed interaction, and the bot log says which.
