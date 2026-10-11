# Troubleshooting

This document uses ASD-STE100 Simplified Technical English. For the full flow, read [how-obo-works.md](how-obo-works.md).

## 1 Identify the token

1. Copy the token.
2. Paste the token into [jwt.ms](https://jwt.ms). The site decodes the token in your browser.
3. Compare the claims with this table.

| Claims | Token type | Corrective action |
| --- | --- | --- |
| `iss: https://api.botframework.com`, `aud: <bot ID>`, `serviceurl` | Service token. It comes from the `Authorization` header of a request to `/api/messages`. | This is not a user token. Get an SSO token with the `Action.Execute` SSO steps. |
| `name`, `preferred_username`, `aud: <a client ID>`, no `scp` | ID token | This is not an access token. Get an access token for the bot API. |
| `aud: <bot ID>` or `api://botid-…`, `scp: access_as_user`, `azp: 1fec8e78…` or `5e3ce6c0…` | SSO token. This is the correct OBO assertion. | Send it in the OBO request. Do not send it to the downstream API. |
| `aud: <API ID>` or `api://<API ID>`, `scp: access_as_user`, `azp: <bot ID>` | OBO token. This is the correct token for the downstream API. | Send it to the API in the header `Authorization: Bearer`. |

## 2 Find the cause of a symptom

| Symptom | Probable cause | Corrective action |
| --- | --- | --- |
| The card shows a Sign in button on the first click, each time. | Teams cannot do SSO. | Make sure that these values are the same: manifest `webApplicationInfo.resource`, Application ID URI and Token Exchange URL. Make sure that `webApplicationInfo.id` is the bot ID. Make sure that the Teams clients have pre-authorization. Make sure that the user installed the app in the personal scope. |
| The bot log shows "returned no tokenExchangeResource". | The OAuth connection has no Token Exchange URL. | Set the Token Exchange URL to `api://botid-<bot ID>`. |
| A consent dialog ("View and accept") opens on the first use. | There is no admin consent, or the Teams clients have no pre-authorization. | Give admin consent for the bot app registration. Add the two Teams client IDs to the scope `access_as_user`. |
| The bot log shows "Silent exchange needs user interaction" and `AADSTS65001`. | The bot has no delegated permission on the downstream API scope, or there is no consent. | Add the delegated permission `access_as_user` of the downstream API. Give admin consent. |
| The bot log shows `AADSTS50076`, `AADSTS50079` or `interaction_required`. | A Conditional Access policy for the downstream API asks for MFA or another control. | The user completes the control with the Sign in button. To prevent the prompt, make the policies for Teams and the API the same. |
| The error card shows `AADSTS53003`. | Conditional Access blocks the access. | Speak to your identity administrator. A sign-in cannot correct this. |
| The error card shows `AADSTS7000215` or `AADSTS7000222`. | The client secret is incorrect or old. | Make a new secret. Set it in `AzureAd:ClientCredentials:0:ClientSecret`. |
| The error card shows `AADSTS70011` or `AADSTS65005`. | The scope in `DownstreamApi:Scopes` is incorrect. | Set the scope to `api://<API ID>/access_as_user` or `api://<API ID>/.default`. |
| The error card shows `AADSTS50013`. | The audience of the assertion is not the app that sends the OBO request. | Send the OBO request with the client ID of the bot. The SSO token is for the bot. |
| The error card shows `AADSTS500133`. | The SSO token expired before the OBO request. | Make sure that the clock of the bot host is correct. |
| The bot log shows "Rejected SSO token" and `IDX10214`. | `Sso:BotAppIdUri` or `AzureAd:ClientId` is not the same as the app registration. | Correct the bot settings. |
| The bot log shows "Rejected SSO token" and "different user". | The `oid` claim of the token is not the same as `activity.from.aadObjectId`. | No action. This check prevents the use of a token for a different user. |
| The result card shows HTTP 401 from the API. | The audience of the token is not the API. Or the bot sent the SSO token instead of the OBO token. | Make sure that `AzureAd:ClientId` of the API is the API ID. Send the OBO token. |
| The result card shows HTTP 403 from the API. | The token has no `scp` claim (app-only token). Or the scope name is not the value in `AzureAd:Scopes`. | Send a user token with the correct scope. |
| A request to `/api/messages` with `curl` gets HTTP 401. | This is correct. Only the Bot Connector can send requests to the bot. It signs each request. | Send messages to the bot through Teams. |
| SSO works in a personal chat. It does not work in a channel. | Teams cannot always do SSO in a channel. Then the bot uses the Sign in fallback. | Do a test of SSO in each type of conversation that you use. |

## 3 Get more information from the logs

To see more detail from the Teams SDK, start the bot with this command:

```bash
Logging__LogLevel__Microsoft.Teams=Debug dotnet run --project src/Bot
```

WARNING: Do not use the `Trace` level in a shared or production environment. At this level, the Teams SDK writes each invoke activity to the log. The SSO token is in `value.authentication.token`.
