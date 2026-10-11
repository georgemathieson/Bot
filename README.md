# Teams bot: Adaptive Card → silent SSO → on-behalf-of → your API

A proof of concept for this flow: a user clicks a button on an Adaptive Card in Teams, and the bot calls
**another service, protected by its own Entra ID app registration**, *as that user*. It uses the OAuth 2.0
**on-behalf-of (OBO)** flow, and the user sees **no sign-in or MFA popup**.

- **Bot:** TypeScript on the [Teams SDK](https://github.com/microsoft/teams.ts) (`@microsoft/teams.apps` 2.1)
- **Auth:** [MSAL Node](https://github.com/AzureAD/microsoft-authentication-library-for-js)
- **Sample downstream API:** Express + `jose`

```
Teams client ──(Action.Execute)──► Bot ──(OBO)──► Entra ID ──► Bot ──(Bearer user token)──► Downstream API
```

## Why your bot only had "a token with the user's name in it"

None of the tokens a bot receives by default can be used for OBO:

| What you looked at | What it really is | Usable for OBO? |
| --- | --- | --- |
| `Authorization: Bearer …` header on the POST to `/api/messages` | A **Bot Connector service token**: `iss` = `https://api.botframework.com`, `aud` = your bot's app ID. It proves *the Bot Framework* called you. It says nothing about the user. | No |
| `activity.from` (`name`, `aadObjectId`) | Identity *hints* about the user. Not a token. | No |
| A token with `name` / `preferred_username` but **no `scp` claim** | Probably an **ID token**. It describes the user to a client app. It doesn't grant access to an API. | No |
| What this POC gets: `aud` = your bot (`<bot-client-id>` or `api://botid-…`), `scp` = `access_as_user`, `azp` = a Teams client ID | A real delegated **access token for your bot's API**, issued by Entra to Teams for this user | **Yes**, this is the OBO assertion |

A bot never receives a user access token unless it **asks Teams for one**. For Adaptive Cards, the
mechanism is the **Universal Actions (`Action.Execute`) SSO handshake**. Microsoft documents it in
[Enable SSO for Adaptive Cards Universal Actions](https://learn.microsoft.com/microsoftteams/platform/task-modules-and-cards/cards/universal-actions-for-adaptive-cards/sso-adaptive-cards-universal-action).

## How it works

```mermaid
sequenceDiagram
    autonumber
    actor U as User
    participant T as Teams client
    participant B as Bot (this repo)
    participant BF as Bot Framework token service
    participant E as Entra ID
    participant API as Downstream API

    U->>T: clicks "Call downstream API" (Action.Execute)
    T->>B: invoke adaptiveCard/action (no token)
    B->>BF: getSignInResource(OAuth connection)
    BF-->>B: sign-in link + tokenExchangeResource {uri: api://botid-<bot>}
    B-->>T: 401 application/vnd.microsoft.activity.loginRequest (with tokenExchangeResource)
    Note over T,E: Silent: Teams is pre-authorized for the bot's access_as_user scope
    T->>E: get token for api://botid-<bot> (Teams' own session, MFA already satisfied)
    E-->>T: SSO token (aud=bot, scp=access_as_user)
    T->>B: same invoke again + value.authentication.token
    B->>B: validate JWT (sig, iss, aud, scp, oid == activity.from.aadObjectId)
    B->>E: OBO: grant_type=jwt-bearer, assertion=SSO token, scope=api://<api>/access_as_user
    E-->>B: access token (aud=downstream API, azp=bot, oid=user)
    B->>API: GET /api/me  Authorization: Bearer <OBO token>
    API-->>B: 200 {user, scopes, calling app}
    B-->>T: 200 updated Adaptive Card
```

Steps 4–9 happen on every click, but the user never sees them. If the OBO call returns
`consent_required` or needs MFA (`AADSTS65001`, `AADSTS50076`, …), the bot replies with a sign-in request
*without* SSO info. Teams then shows a **Sign in** button (one time). After sign-in, Teams re-sends the
action with a magic code in `value.state`, which the bot redeems. If the SSO token is unusable, the bot
replies `412 preconditionFailed`, and Teams also falls back to the button.

The reusable part is [`withUserToken()`](bot/src/sso/with-user-token.ts). Wrap any `Action.Execute`
handler with it, and the handler receives an access token for your API:

```ts
app.on('card.action.callApi', withUserToken(options, async (ctx, token) => {
  const result = await callDownstreamApi(token.accessToken);
  return { statusCode: 200, type: 'application/vnd.microsoft.card.adaptive', value: resultCard(result) };
}));
```

## What makes it popup-free

All of these must be true. Missing any one of them gives you a consent dialog or a Sign in button.

1. The bot's app registration **exposes an API** with Application ID URI `api://botid-<bot-client-id>` and
   scope `access_as_user`.
2. The two **Teams client IDs are pre-authorized** on that scope:
   - `1fec8e78-bce4-4aaf-ab1b-5451cc387264` (desktop/mobile)
   - `5e3ce6c0-2b1f-4285-8d4b-75ee78787346` (web)
3. The manifest's `webApplicationInfo` has `id` = bot client ID and `resource` = that same Application ID URI.
4. The bot app registration has a **delegated API permission on the downstream API's scope**, and an admin has
   **granted tenant-wide (admin) consent**. Without it, OBO fails with `AADSTS65001` and the user gets a
   one-time Sign in.
5. The Azure Bot's **OAuth connection** (Entra ID v2) has **Token Exchange URL** = the same Application ID URI.
   The bot uses it for the loginRequest and for the sign-in fallback.
6. The app is installed for the user in **personal scope**. Teams prompts to install it if not.

On MFA: the SSO token comes from the user's existing Teams session, so MFA is already satisfied and
OBO carries it forward (`amr` includes `mfa`). You only get an MFA prompt if a **Conditional Access**
policy targets the downstream API with requirements the Teams session doesn't meet. That surfaces
as `interaction_required`, and the bot falls back to the Sign in button.

Step-by-step portal and CLI setup: **[docs/azure-setup.md](docs/azure-setup.md)**.
When something doesn't work: **[docs/troubleshooting.md](docs/troubleshooting.md)**.

## Two ways to do the exchange (`TOKEN_STRATEGY`)

| | `obo` (default) | `token-service` |
| --- | --- | --- |
| Who calls Entra's OBO endpoint | The bot, via MSAL `acquireTokenOnBehalfOf` | The Bot Framework token service, using the OAuth connection's client ID/secret and Scopes |
| Token storage | None (MSAL in-memory cache only). Each click repeats the silent 401 round trip. | Token service stores and refreshes the user's token, so later clicks skip SSO |
| Multiple downstream APIs | Yes, one SSO token can be exchanged for any number of APIs | One OAuth connection per API (scopes are fixed per connection) |
| Conditional Access claims challenge | Visible to your code (`InteractionRequiredError.claims`) | Hidden inside the service |

Both use the same Azure configuration. The OAuth connection's **Scopes** must be the downstream scope,
because the sign-in fallback goes through it in both modes.

## Which SDK? (TeamsFx vs "the GitHub AI one")

- **TeamsFx SDK** is in deprecation mode, with community-only support. Its `TeamsBotSsoPrompt` and
  `OnBehalfOfUserCredential` implemented this same pattern. Don't start new work on it.
- **Teams AI library**, the GitHub one, was renamed **Teams SDK** (v2). It's Microsoft's current
  recommendation for Teams-only bots, and it's what this POC uses. It provides the activity routing
  (`card.action.*`), the invoke response types and the token-service client. The SSO-in-card handshake
  is implemented explicitly here, because the SDK's built-in `OAuthFlow` only covers OAuth *cards*
  (`signin/tokenExchange`), not `Action.Execute` login requests.
- **Microsoft 365 Agents SDK** replaces the retired Bot Framework SDK. Choose it if the same agent must
  also run in Microsoft 365 Copilot or other channels. The protocol here (401 loginRequest → SSO token
  → OBO) is identical, so `bot/src/sso/` ports over directly.

## Run it

Prerequisites: Node 22.12+, an Entra tenant where you can create app registrations and grant admin
consent, an Azure subscription for the Azure Bot resource, a public HTTPS tunnel
([dev tunnels](https://learn.microsoft.com/azure/developer/dev-tunnels/) or ngrok), and permission to
upload custom apps in Teams.

```bash
npm install
npm test                 # unit + end-to-end tests (fake Entra tenant, no Azure needed)

# after docs/azure-setup.md:
cp downstream-api/.env.example downstream-api/.env   # fill in
cp bot/.env.example bot/.env                         # fill in
npm run dev:api          # http://localhost:5000
npm run dev:bot          # http://localhost:3978/api/messages, expose it via your tunnel
npm run package:app      # appPackage/build/appPackage.zip, upload in Teams > Apps > Manage your apps
```

Message the bot anything to get the card, then select **Call downstream API**. The result card shows
the user as the API saw them, plus (with `SHOW_DIAGNOSTICS=true`) the decoded claims of both tokens.
Those claims show the chain: SSO token `azp` = Teams → OBO token `azp` = your bot → API `aud`.
Send `signout` to clear any token held in the Bot Framework token store.

## Layout

```
bot/
  src/sso/with-user-token.ts   Action.Execute SSO handshake (401 loginRequest, SSO token, 412, magic code)
  src/sso/downstream-token.ts  SSO token -> downstream token: MSAL OBO or token-service exchange
  src/sso/sso-token.ts         validates the SSO token (+ checks it belongs to the clicking user)
  src/sso/token-service.ts     Bot Framework token service calls (sign-in resource, magic code, exchange)
  src/app.ts                   routes: welcome card, card.action.callApi, unknown actions
  src/cards.ts                 Adaptive Cards (Action.Execute, version 1.5)
downstream-api/                sample protected API: validates iss/aud/scp, rejects app-only tokens
appPackage/                    Teams manifest template (webApplicationInfo) + icons
e2e/                           real bot HTTP endpoint + real API, with Entra and token service simulated
docs/                          Azure setup, troubleshooting
scripts/                       Entra setup script, app packaging
```

## Before you take this further

- **Secrets:** replace the client secret with a certificate, or with a federated identity credential
  backed by a managed identity. MSAL supports `clientCertificate` and `clientAssertion`, and the Teams SDK
  supports `managedIdentityClientId`.
- **Scale-out:** with `obo`, plug a distributed token cache into MSAL. With `token-service`, it's already
  external.
- **Group chats and channels:** Microsoft documents `Action.Execute` sign-in for group chats and channels,
  but test SSO there explicitly. Use `refresh` with `userIds` if you want user-specific card views.
- Set `SHOW_DIAGNOSTICS=false`. Never log or display raw tokens. This code doesn't, but the Teams SDK's
  `LOG_LEVEL=debug` logs whole inbound activities, SSO token included.
- Your real downstream service must validate tokens the way `downstream-api/src/entra-jwt.ts` does
  (issuer, audience, `scp`, and reject app-only tokens), then authorize on `oid`, `scp` and roles.
