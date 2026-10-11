# Troubleshooting

Paste any token into [jwt.ms](https://jwt.ms) (it decodes locally) and compare it with this table first.

## Which token is this?

| Claims | It is | Fix |
| --- | --- | --- |
| `iss: https://api.botframework.com`, `aud: <bot ID>`, `serviceurl` | Bot Connector → bot service token, from the `Authorization` header on `/api/messages` | Not a user token. Do the `Action.Execute` SSO handshake. |
| `name`, `preferred_username`, `aud: <some client ID>`, **no `scp`** | ID token | Not an API token. You need an access token for your bot's API. |
| `aud: <bot ID>` or `api://botid-…`, `scp: access_as_user`, `azp: 1fec8e78…`/`5e3ce6c0…` | **Teams SSO token**: correct OBO assertion | Exchange it with OBO. Don't send it to the downstream API. |
| `aud: <API ID>` or `api://<API ID>`, `scp: access_as_user`, `azp: <bot ID>` | **OBO result**: correct token for the downstream API | Send this as `Authorization: Bearer` to the API |

## Symptoms

| Symptom | Likely cause |
| --- | --- |
| Card shows a **Sign in** button on the first click, every time | Teams couldn't do silent SSO. Check (a) manifest `webApplicationInfo.resource` == Application ID URI == OAuth connection Token Exchange URL; (b) `webApplicationInfo.id` == bot ID; (c) Teams clients pre-authorized on `access_as_user`; (d) the app is installed in personal scope. The bot logs a warning if the connection returns no `tokenExchangeResource`. |
| **Consent dialog** ("View and accept") on first use | No admin consent on the bot app reg, and/or Teams clients not pre-authorized. |
| Bot log: `Silent exchange needs user interaction … AADSTS65001` | The bot app has no (admin-consented) delegated permission on the downstream API scope. |
| Bot log: `… AADSTS50076` / `AADSTS50079` / `interaction_required` | Conditional Access on the downstream API wants MFA or another control the Teams session doesn't satisfy. The user completes it via the Sign in button. To keep it silent, align the CA policies for Teams and the API. |
| Error card `AADSTS53003` | Blocked by Conditional Access. Not fixable by signing in. |
| Error card `AADSTS7000215` / `AADSTS7000222` | Wrong or expired `AzureAd:ClientCredentials:0:ClientSecret`. |
| Error card `AADSTS70011` / `AADSTS65005` | `DownstreamApi:Scopes` is wrong. Use `api://<API ID>/access_as_user` (or `api://<API ID>/.default`). |
| Error card `AADSTS50013` | Assertion audience mismatch. OBO must be done with the client ID the SSO token was issued **to** (the bot app). |
| Error card `AADSTS500133` | SSO token expired before the exchange (clock skew on the bot host?). |
| Bot log: `Rejected SSO token: IDX10214: Audience validation failed` → card falls back to Sign in | `Sso:BotAppIdUri` / `AzureAd:ClientId` in the bot's settings don't match the app registration. |
| Bot log: `Rejected SSO token: … different user` | Token's `oid` ≠ `activity.from.aadObjectId`. That's the replay check working. |
| Result card says the API returned **401** | `AzureAd:ClientId` in the API's settings doesn't match the token's audience, or you sent the SSO token instead of the OBO token. |
| Result card says the API returned **403** | Token has no `scp` (app-only) or a different scope name than `AzureAd:Scopes`. |
| `curl` to `/api/messages` gets **401** | Expected. Only the Bot Connector, with a signed JWT, may call the bot. Test through Teams. |
| Works in a personal chat but not in a channel | Test channel SSO separately. Teams may not complete silent SSO in every channel context. The bot then gets the Sign in fallback. |

## Turn on SDK logging

```bash
Logging__LogLevel__Microsoft.Teams=Debug dotnet run --project src/Bot
```

At `Trace`, the Teams SDK logs every inbound invoke activity. That includes the SSO token in
`value.authentication.token`. Use `Trace` only locally, never in a shared or production environment.
