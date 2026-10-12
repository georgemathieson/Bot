# Troubleshooting

This document uses ASD-STE100 Simplified Technical English. For the full flow, read [how-obo-works.md](how-obo-works.md). Each cause has a footnote to its Microsoft source. A dagger (†) marks a source that only a search summary confirms.

## 1 Identify the token

1. Copy the token.
2. Paste the token into [jwt.ms](https://jwt.ms). The site decodes the token in your browser.[^jwt-ms]
3. Compare the claims with this table.

| Claims | Token type | Corrective action |
| --- | --- | --- |
| `iss: https://api.botframework.com`, `aud: <bot ID>`, `serviceurl`[^sdk-issuer] | Service token. It comes from the `Authorization` header of a request to `/api/messages`.[^connector-auth] | This is not a user token. Get an SSO token with the `Action.Execute` SSO steps.[^ua-handle] |
| `name`, `preferred_username`, `aud: <a client ID>`, no `scp` | ID token | This is not an access token. Get an access token for the bot API. Only a user access token has `scp`.[^claims] |
| `aud: <SSO app ID>` or `api://botid-…`, `scp: access_as_user`, `azp: 1fec8e78…` or `5e3ce6c0…`[^bot-clients] | SSO token. This is the correct OBO assertion. | Send it in the OBO request.[^obo-request] Do not send it to the downstream API.[^tokens-validate] |
| `aud: <API ID>` or `api://<API ID>`, `scp: access_as_user`, `azp: <SSO app ID>` | OBO token. This is the correct token for the downstream API. | Send it to the API in the header `Authorization: Bearer`.[^obo-protocol] |

## 2 Find the cause of a symptom

| Symptom | Probable cause | Corrective action |
| --- | --- | --- |
| The card shows a Sign in button on the first click, each time. | Teams cannot do SSO. If the Teams client cannot use the `tokenExchangeResource`, it shows the Sign in button.[^ua-handle] | Make sure that these values are the same: manifest `webApplicationInfo.resource`, Application ID URI and Token Exchange URL.[^bot-manifest][^bot-oauth] Make sure that `webApplicationInfo.id` is the SSO app ID. Make sure that the Teams clients have pre-authorization.[^bot-clients] Make sure that the user installed the app in the personal scope.[^ua-handle] |
| The bot log shows "returned no tokenExchangeResource". | The OAuth connection has no Token Exchange URL. | Set the Token Exchange URL to `api://botid-<bot ID>`.[^bot-oauth] |
| A consent dialog ("View and accept") opens on the first use. | The user did not give consent for the bot yet.[^ua-consent] There is no admin consent, or the Teams clients have no pre-authorization. | Give admin consent for the SSO app registration.[^grant-consent] Add the two Teams client IDs to the scope `access_as_user`.[^bot-clients] |
| The bot log shows "Silent exchange needs user interaction" and `AADSTS65001`. | There is no consent for the bot to use the downstream API.[^errors] | Add the delegated permission `access_as_user` of the downstream API. Give admin consent.[^obo-admin] |
| The bot log shows `AADSTS50076`, `AADSTS50079` or `interaction_required`. | A Conditional Access policy for the downstream API asks for MFA or another control.[^errors][^obo-error] | The user completes the control with the Sign in button. To prevent the prompt, make the policies for Teams and the API the same.[^ca-obo] |
| The error card shows `AADSTS53003`. | Conditional Access blocks the access.[^errors] | Speak to your identity administrator. A sign-in cannot correct this. |
| The error card shows `AADSTS70021`. | The SSO app registration has no federated credential that agrees with the managed identity token. Or a new credential is not active yet.[^fic-propagate] | Make sure that the subject is the object (principal) ID of the managed identity, not its client ID. Make sure of the issuer and the audience `api://AzureADTokenExchange`.[^fic-values] Wait some minutes after a change. |
| The error card shows `managed_identity_unreachable_network` or `managed_identity_request_failed`. | The bot cannot get a managed identity token. On your computer, there is no managed identity.[^mi-overview] In Azure, the host does not have the managed identity. | On your computer, use a client secret and leave `SsoApp:ClientId` empty (azure-setup.md, step 7). In Azure, assign the managed identity to the host.[^sdk-uami] |
| The bot log shows "treating ClientId … as a User-Assigned Managed Identity" on your computer. | `AzureAd` has no `ClientCredentials`, so the Teams SDK uses a managed identity.[^sdk-uami-config] | Set `AzureAd:ClientCredentials:0:SourceType` and `AzureAd:ClientCredentials:0:ClientSecret` in user-secrets. |
| The bot stops at startup with "ClientCredentials is empty". | The bot has no credential for the OBO request. With a managed identity bot, `SsoApp` is not set. | Set `SsoApp:ClientId` to the SSO app ID. The bot then uses its managed identity as the credential of the SSO app. |
| The installation of the Teams app fails with "IsUserAuthorizedToGrantPersonalScopeResourceSpecificPermissionsRequestV2". | `webApplicationInfo.id` is the client ID of the managed identity.[^uami-manifest] | Make the app package with the SSO app ID as the second value: `dotnet run scripts/package-app.cs -- <bot ID> <SSO app ID>`. |
| The error card shows `AADSTS7000215` or `AADSTS7000222`. | The client secret is incorrect or expired. This occurs only on your computer, or in an OAuth connection with a secret.[^errors] | Make a new secret. Set it in `AzureAd:ClientCredentials:0:ClientSecret`, or in the OAuth connection. |
| The error card shows `AADSTS70011` or `AADSTS65005`. | The scope is not valid, or the app registration does not list the downstream API.[^errors] | Set `DownstreamApi:Scopes` to `api://<API ID>/access_as_user`. Add the API permission to the SSO app registration. |
| The error card shows `AADSTS50013`. | The assertion is not valid (InvalidAssertion). The error reference gives several causes.[^errors] One cause is an assertion that has a different audience.[^obo-request] | Make sure that the SSO token is for the SSO app. Make sure that the bot sends the OBO request with the SSO app ID. |
| The error card shows `AADSTS500133`. | The SSO token expired before the OBO request.[^errors] | Make sure that the clock of the bot host is correct. |
| The bot log shows "Rejected SSO token" and `IDX10214`. | The audience of the SSO token is not the SSO app ID or `Sso:BotAppIdUri`.[^tokens-validate] The SSO app ID is `SsoApp:ClientId`, or `AzureAd:ClientId` if `SsoApp:ClientId` is empty. | Make the bot settings the same as the SSO app registration. |
| The bot log shows "Rejected SSO token" and "different user". | The `oid` claim of the token is not the same as `activity.from.aadObjectId`. | No action. This check prevents the use of a token for a different user. |
| The result card shows HTTP 401 from the API. | The audience of the token is not the API.[^tokens-validate] Or the bot sent the SSO token instead of the OBO token. | Make sure that `AzureAd:ClientId` of the API is the API ID.[^miw-config] Send the OBO token. |
| The result card shows HTTP 403 from the API. | The token has no `scp` claim (app-only token).[^claims] Or the scope name is not the value in `AzureAd:Scopes`.[^miw-scopes] | Send a user token with the correct scope. |
| A request to `/api/messages` with `curl` gets HTTP 401. | This is correct. The bot accepts only requests that have a valid token from the Bot Connector.[^connector-auth] | Send messages to the bot through Teams. |
| SSO works in a chat. It does not work in a channel. | Microsoft does not support bot SSO in channels.[^bot-sso-scope] The bot then uses the Sign in fallback. | No action. Use SSO in one-on-one chats and group chats. |

## 3 Get more information from the logs

To see more detail from the Teams SDK, start the bot with this command:

```bash
Logging__LogLevel__Microsoft.Teams=Debug dotnet run --project src/Bot
```

WARNING: Do not use the `Trace` level in a shared or production environment. At this level, the Teams SDK writes each invoke activity to the log.[^sdk-trace] The SSO token is in `value.authentication.token`.

[^jwt-ms]: [Access tokens in the Microsoft identity platform](https://learn.microsoft.com/entra/identity-platform/access-tokens). "For validation and debugging purposes *only*, developers can decode JWTs using a site like jwt.ms."
[^sdk-issuer]: [Teams SDK for .NET source: BotConfig.cs, lines 23 to 27](https://github.com/microsoft/teams.net/blob/v2.1.1/src/Microsoft.Teams.Core/Hosting/BotConfig.cs#L23-L27). The default issuer is `https://api.botframework.com`.
[^connector-auth]: [Authentication with the Bot Connector API](https://learn.microsoft.com/azure/bot-service/rest-api/bot-framework-rest-connector-authentication) †. The bot must validate the token from the Bot Connector.
[^ua-handle]: [Add code to enable SSO for Adaptive Cards Universal Actions: Add code to handle an access token](https://learn.microsoft.com/microsoftteams/platform/task-modules-and-cards/cards/universal-actions-for-adaptive-cards/sso-adaptive-cards-universal-action#add-code-to-handle-an-access-token). If the client ignores the `tokenExchangeResource`, it starts the nominal sign-in. The page also gives the install prompt for the personal scope.
[^ua-consent]: [Add code to enable SSO for Adaptive Cards Universal Actions: Consent dialog for getting access token](https://learn.microsoft.com/microsoftteams/platform/task-modules-and-cards/cards/universal-actions-for-adaptive-cards/sso-adaptive-cards-universal-action#consent-dialog-for-getting-access-token).
[^claims]: [Access token claims reference: Payload claims](https://learn.microsoft.com/entra/identity-platform/access-token-claims-reference#payload-claims). `scp` is "Only included for user tokens".
[^bot-clients]: [Configure your bot app in Microsoft Entra ID: To configure authorized client application](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-register-aad?tabs=botid#to-configure-authorized-client-application). Lists the Teams client IDs.
[^obo-request]: [On-Behalf-Of flow: First case: Access token request with a shared secret](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow#first-case-access-token-request-with-a-shared-secret). The assertion "must have an audience (`aud`) claim of the app making this OBO request".
[^obo-protocol]: [On-Behalf-Of flow: Protocol diagram](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow#protocol-diagram). "Token B is set by API A in the authorization header of the request to API B."
[^obo-error]: [On-Behalf-Of flow: Error response example](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow#error-response-example).
[^obo-admin]: [On-Behalf-Of flow: Admin consent](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow#admin-consent).
[^ca-obo]: [Developer guidance for Conditional Access: Scenario: App performing the on-behalf-of flow](https://learn.microsoft.com/entra/identity-platform/v2-conditional-access-dev-guide#scenario-app-performing-the-on-behalf-of-flow).
[^tokens-validate]: [Access tokens in the Microsoft identity platform: Validate tokens](https://learn.microsoft.com/entra/identity-platform/access-tokens#validate-tokens). Web APIs "must only accept tokens containing one of their AppId URIs as the `aud` claim."
[^bot-manifest]: [Update app manifest for SSO: webApplicationInfo property](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-manifest#webapplicationinfo-property).
[^bot-oauth]: [Configure your bot app in Microsoft Entra ID: To update OAuth connection](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-register-aad?tabs=botid#to-update-oauth-connection).
[^grant-consent]: [Grant tenant-wide admin consent to an application: Grant admin consent in App registrations pane](https://learn.microsoft.com/entra/identity/enterprise-apps/grant-admin-consent#grant-admin-consent-in-app-registrations-pane).
[^errors]: [Microsoft Entra authentication and authorization error codes: AADSTS error codes](https://learn.microsoft.com/entra/identity-platform/reference-error-codes#aadsts-error-codes). Describes each AADSTS code in this table.
[^miw-config]: [Protected web API: Code configuration: JwtBearer configuration](https://learn.microsoft.com/entra/identity-platform/scenario-protected-web-api-app-configuration#jwtbearer-configuration). With the default App ID URI, you do not have to set the audience.
[^miw-scopes]: [Protected web API: Verify scopes and app roles: Verify scopes in APIs called on behalf of users](https://learn.microsoft.com/entra/identity-platform/scenario-protected-web-api-verification-scope-app-roles#verify-scopes-in-apis-called-on-behalf-of-users).
[^bot-sso-scope]: [Enable SSO for your bot app](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-overview). "SSO for a bot app in Teams is supported in one-on-one and group chat scope, and not supported in channel scope."
[^sdk-trace]: [Teams SDK for .NET source: Router.cs, line 188](https://github.com/microsoft/teams.net/blob/v2.1.1/src/Microsoft.Teams.Apps/Routing/Router.cs#L188). At `Trace` level, the router writes the invoke activity JSON to the log.
[^fic-values]: [Configure an application to trust a managed identity: Important considerations and restrictions](https://learn.microsoft.com/entra/workload-id/workload-identity-federation-config-app-trust-managed-identity#important-considerations-and-restrictions). The subject is the "Object (Principal) ID" of the managed identity. The audience is `api://AzureADTokenExchange`.
[^fic-propagate]: [Important considerations and restrictions for federated identity credentials: Time for federated credential changes to propagate](https://learn.microsoft.com/entra/workload-id/workload-identity-federation-considerations#time-for-federated-credential-changes-to-propagate). "AADSTS70021: No matching federated identity record found for presented assertion."
[^mi-overview]: [What is managed identities for Azure resources?: What are managed identities?](https://learn.microsoft.com/entra/identity/managed-identities-azure-resources/overview#what-are-managed-identities). "A managed identity is an identity that can be assigned to an Azure compute resource".
[^sdk-uami]: [Teams SDK documentation source: User Assigned Managed Identity Setup, lines 25 to 33](https://github.com/microsoft/teams-sdk/blob/0187ab0b2c43a9f21e9398a593835802d62fbecb/teams.md/docs/main/teams/app-authentication/user-managed-identity.md#L25-L33). The managed identity "must be assigned to the service running your application".
[^sdk-uami-config]: [Teams SDK documentation source: C# app authentication, line 94](https://github.com/microsoft/teams-sdk/blob/0187ab0b2c43a9f21e9398a593835802d62fbecb/teams.md/src/components/include/essentials/app-authentication/csharp.incl.md#L94). A `ClientId` without a `ClientSecret` selects the user-assigned managed identity.
[^uami-manifest]: [Update app manifest for SSO: webApplicationInfo property](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-manifest#webapplicationinfo-property). Gives this installation error for a managed identity ID in `webApplicationInfo`.
