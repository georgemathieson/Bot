# Azure and Entra ID setup

This document uses ASD-STE100 Simplified Technical English. Each step has a footnote to its Microsoft source. A dagger (†) marks a source that only a search summary confirms.

## 0 Before you start

You must have these items:

- A Microsoft Entra ID tenant. You must have permission to make app registrations and to give admin consent.
- An Azure subscription for the managed identity, the Azure Bot resource and the host of the bot.
- An Azure host for the bot that can use a managed identity, for example Azure App Service or Azure Container Apps.[^mi-overview]
- The .NET 10 SDK. If you use the setup script, you must also have the Azure CLI.
- Permission to upload custom apps in Microsoft Teams.[^apps-upload]

### 0.1 The identities

The bot uses three Entra ID identities. In Azure, the bot uses no secret. Only the OAuth connection can have a secret (step 5).

| Identity | Function |
| --- | --- |
| User-assigned managed identity | The identity of the Azure Bot. The Teams SDK uses it for Bot Framework traffic.[^sdk-uami-config] The identity is also the credential of the SSO app registration.[^mi-fic] |
| SSO app registration | Teams gets the SSO token for this app. The bot sends the OBO request as this app. A managed identity cannot expose an API scope, so this app is necessary.[^uami-sso] |
| Downstream API app registration | The "other service". The OBO token is for this app. |

In this document, these names identify the client IDs:

- "Bot ID" is the client ID of the managed identity. It is also the Microsoft App ID of the Azure Bot.
- "SSO app ID" is the Application (client) ID of the SSO app registration.
- "API ID" is the Application (client) ID of the downstream API app registration.

These values must be the same in all of their locations:

| Value | Locations |
| --- | --- |
| `<bot ID>` | Managed identity, client ID. Azure Bot, Microsoft App ID. Manifest, `bots[0].botId`. Bot settings `AzureAd:ClientId` and `SsoApp:ClientCredentials:0:ManagedIdentityClientId`. |
| `<SSO app ID>` | SSO app registration, client ID. Manifest, `webApplicationInfo.id`. OAuth connection, Client id. Bot setting `SsoApp:ClientId`. |
| `api://botid-<SSO app ID>` | SSO app registration, Expose an API, Application ID URI. Manifest, `webApplicationInfo.resource`. OAuth connection, Token Exchange URL. Bot setting `Sso:BotAppIdUri` (default value). |
| `api://<API ID>/access_as_user` | Downstream API app registration, scope. SSO app registration, API permissions. OAuth connection, Scopes. Bot setting `DownstreamApi:Scopes`. |

### 0.2 Fast path

The script `scripts/setup-entra.sh` does steps 1 to 3, and it gives admin consent. At the end, it shows the settings for the bot host and the commands for the next steps.

1. Sign in to the Azure CLI with `az login`.
2. Read `scripts/setup-entra.sh`.
3. Run `scripts/setup-entra.sh`. Set `RESOURCE_GROUP` and `LOCATION` to change the location of the managed identity.
4. Do steps 4 to 9 of this document.

If `LOCAL_DEV` is `true` (the default), the script also does step 3.6. Set `LOCAL_DEV=false` to make no secret.

CAUTION: Read the script before you run it. The script makes new app registrations each time that it runs. The script did not run against a real tenant. It ran only against a test stub of `az`.

## 1 Make the downstream API app registration

The downstream API is the "other service". If you already have this service, make sure that its app registration agrees with this step.

1. In the Microsoft Entra admin center, go to **App registrations**.
2. Select **New registration**.
3. Type the name `poc-downstream-api`.
4. Set **Supported account types** to **Accounts in this organizational directory only**.
5. Select **Register**.
6. Go to **Expose an API**.
7. Set the **Application ID URI** to `api://<API ID>`. This is the default value.
8. Select **Add a scope**.[^expose-api]
9. Type the scope name `access_as_user`.
10. Set **Who can consent** to **Admins and users**.
11. Set **State** to **Enabled**, then select **Add scope**.
12. Go to **Manifest**.
13. Set `requestedAccessTokenVersion` to `2`.[^manifest-version] The legacy manifest editor shows this property as `accessTokenAcceptedVersion`.[^manifest-legacy]

NOTE: Step 13 is optional. The sample API accepts v1 and v2 tokens.

Keep the settings for `src/DownstreamApi` in user-secrets:[^user-secrets]

```bash
dotnet user-secrets set "AzureAd:TenantId" "<tenant ID>" --project src/DownstreamApi
dotnet user-secrets set "AzureAd:ClientId" "<API ID>" --project src/DownstreamApi
```

## 2 Make the managed identity

1. In the Azure portal, select **Create a resource**.
2. Find and select **User Assigned Managed Identity**.
3. Select the subscription, the resource group and the region. Type the name `id-teams-sso-bot`.
4. Select **Review + create**, then select **Create**.
5. Go to the new resource, then go to **Overview**.
6. Record the **Client ID** (the bot ID), the **Object (principal) ID** and the resource ID.

You can also use the Azure CLI.[^azcli-identity]

```bash
az group create -n rg-teams-sso-poc -l westeurope
az identity create -g rg-teams-sso-poc -n id-teams-sso-bot --query "[clientId, principalId, id]" -o tsv
```

NOTE: If you make the Azure Bot first in the portal, the portal can make the managed identity for you.[^sdk-uami] This procedure makes the identity first, because step 3.5 must have its object ID.

## 3 Make the SSO app registration

1. In the Microsoft Entra admin center, go to **App registrations**.
2. Select **New registration**.
3. Type the name `poc-teams-sso-bot`.
4. Set **Supported account types** to **Accounts in this organizational directory only**.
5. Select **Register**.

Microsoft does not make an app registration for a bot with a managed identity. You must make this app registration, and use its client ID in `webApplicationInfo.id`.[^uami-sso] Do not use the client ID of the managed identity in `webApplicationInfo`. If you do, the installation of the Teams app fails.[^uami-manifest] An app registration in `webApplicationInfo` that is not the Azure Bot is a "valid and supported configuration".[^separate-app]

### 3.1 Expose the API for Teams SSO

1. Go to **Expose an API**.
2. Set the **Application ID URI** to `api://botid-<SSO app ID>`. Do not keep the default value `api://<SSO app ID>`.[^bot-uri]
3. Select **Add a scope**.
4. Type the scope name `access_as_user`.
5. Set **Who can consent** to **Admins and users**.
6. Set **State** to **Enabled**, then select **Add scope**.[^bot-scope]
7. Select **Add a client application**.
8. Type the client ID `1fec8e78-bce4-4aaf-ab1b-5451cc387264`. This is the Teams desktop and mobile client.
9. Select the `access_as_user` scope, then select **Add application**.
10. Do steps 7 to 9 again with the client ID `5e3ce6c0-2b1f-4285-8d4b-75ee78787346`. This is the Teams web client.

This pre-authorization lets Teams get the SSO token without a consent prompt.[^bot-clients][^expose-api]

### 3.2 Set the token version

1. Go to **Manifest**.
2. Set `requestedAccessTokenVersion` to `2`.[^bot-version]

### 3.3 Add the redirect URI for the token service

1. Go to **Authentication**.
2. Select **Add a platform**, then select **Web**.
3. Type the redirect URI `https://token.botframework.com/.auth/web/redirect`.[^redirect]
4. Select **Configure**.

The Bot Framework token service uses this URI for the sign-in fallback. If your bot uses a regional token service, use its host. An example is `europe.token.botframework.com`.[^bot-redirect]

### 3.4 Give the SSO app permission to use the downstream API

1. Go to **API permissions**.
2. Select **Add a permission**.
3. Select **APIs my organization uses**, then select `poc-downstream-api`.
4. Select **Delegated permissions**, then select `access_as_user`.
5. Select **Add permissions**.
6. Optional: add the Microsoft Graph delegated permissions `openid`, `profile` and `offline_access`. Then admin consent also covers the fallback sign-in.
7. Select **Grant admin consent for \<tenant\>**.[^grant-consent]

Admin consent lets the bot get the OBO token for all users without a prompt.[^obo-admin] If there is no consent, the OBO request fails with `AADSTS65001`.[^errors] Then each user sees a Sign in button one time.

### 3.5 Add the federated credential for the managed identity

This credential lets the bot use a managed identity token instead of a secret when it sends the OBO request as the SSO app.[^mi-fic]

1. Go to **Certificates & secrets**, then go to **Federated credentials**.
2. Select **Add credential**.
3. Set **Federated credential scenario** to **Managed Identity**.[^fic-create]
4. Select the managed identity from step 2. Only a user-assigned managed identity can be a credential.[^fic-create]
5. Make sure that the values agree with this table:[^fic-values]

| Field | Value |
| --- | --- |
| Issuer | `https://login.microsoftonline.com/<tenant ID>/v2.0`. The portal sets this value. |
| Subject | The **Object (principal) ID** of the managed identity. It is not the client ID. |
| Audience | `api://AzureADTokenExchange` |
| Name | `bot-managed-identity` |

6. Select **Add**.

You can also use the Azure CLI. In the JSON file, set the subject to the object (principal) ID of the managed identity.[^fic-create]

```bash
az ad app federated-credential create --id <SSO app object ID> --parameters credential.json
```

CAUTION: Entra ID does not examine the issuer, subject and audience when you add the credential. An incorrect value shows only when the bot sends the OBO request.[^fic-values] A new credential can also take some minutes to become active. Until then, the OBO request fails with `AADSTS70021`.[^fic-propagate]

The managed identity and the SSO app registration must be in the same tenant.[^fic-values] The audience is different in the sovereign clouds. For these clouds, set the audience in the credential and in `SsoApp:ClientCredentials:0:TokenExchangeUrl` to the same value.[^fic-values]

### 3.6 Optional: add a client secret to run the bot on your computer

A managed identity operates only on an Azure resource that has the identity.[^mi-overview] On your computer, the bot uses the SSO app registration as its bot identity, with a client secret. Step 7 tells you how.

1. Go to **Certificates & secrets**.
2. Select **New client secret**.
3. Copy the secret value.[^bot-secret]
4. Keep the bot settings in user-secrets.

CAUTION: Do not put the client secret in `appsettings.json`. All people with access to the repository can read its files. Microsoft recommends that you keep secrets out of source code.[^user-secrets]

```bash
dotnet user-secrets set "AzureAd:TenantId" "<tenant ID>" --project src/Bot
dotnet user-secrets set "AzureAd:ClientId" "<SSO app ID>" --project src/Bot
dotnet user-secrets set "AzureAd:ClientCredentials:0:SourceType" "ClientSecret" --project src/Bot
dotnet user-secrets set "AzureAd:ClientCredentials:0:ClientSecret" "<secret>" --project src/Bot
dotnet user-secrets set "DownstreamApi:Scopes:0" "api://<API ID>/access_as_user" --project src/Bot
```

Do not set `SsoApp:ClientId` on your computer. If `SsoApp:ClientId` is empty, the bot uses the `AzureAd` app registration and its secret for SSO and OBO.

## 4 Make the Azure Bot resource

1. In the Azure portal, select **Create a resource**.
2. Find and select **Azure Bot**.
3. Set **Type of App** to **User-Assigned Managed Identity**.[^bot-types][^sdk-uami]
4. Select the managed identity from step 2.
5. Select **Create**.
6. Go to **Configuration** in the new resource.
7. Make sure that **Microsoft App ID** is the bot ID.
8. Set **Messaging endpoint** to `https://<bot host>/api/messages`.[^bot-endpoint] The bot host is the Azure host from step 6.
9. Go to **Channels**, then add **Microsoft Teams**.

You can also use the Azure CLI. The parameter names come from the Azure CLI source code.[^azcli-bot] Run each command with `--help` before you use it.

```bash
az bot create -g rg-teams-sso-poc -n <unique-bot-name> --app-type UserAssignedMSI \
  --appid <bot ID> --tenant-id <tenant ID> --msi-resource-id <managed identity resource ID> \
  --endpoint https://<bot host>/api/messages
az bot msteams create -g rg-teams-sso-poc -n <unique-bot-name>
```

NOTE: Microsoft deprecated multi-tenant bots in Azure Bot Service. New bots must be single tenant or have a user-assigned managed identity.[^bot-types][^multitenant]

## 5 Add the OAuth connection

The OAuth connection uses the SSO app registration, not the managed identity. The OAuth connection must have a credential of the SSO app registration. Use one of these two service providers:

| Service provider | Credential | Source |
| --- | --- | --- |
| **AAD v2 with Federated Credentials** | A second federated credential on the SSO app registration, for the Bot Framework token service. There is no secret. | [^bot-fic][^agents-fic] |
| **Azure Active Directory v2** | A client secret of the SSO app registration. The secret is in the Azure Bot only, not in the bot code. | [^bot-oauth][^aadv2-connection] |

For **AAD v2 with Federated Credentials**, do the procedure from Microsoft.[^bot-fic] This POC did not do a test of this service provider, and the Microsoft page is not available to this POC. Then set the fields in this table, without the client secret.

1. In the Azure Bot resource, go to **Configuration**.
2. Select **Add OAuth Connection Settings**.
3. Type the values in this table:[^bot-oauth][^aadv2-connection]

| Field | Value |
| --- | --- |
| Name | `sso`. This is the bot setting `Sso:OAuthConnectionName`. |
| Service Provider | One of the two service providers in the table before |
| Client id | The SSO app ID |
| Client secret | Only for **Azure Active Directory v2**. Make the secret as in step 3.6. |
| Token Exchange URL | `api://botid-<SSO app ID>` |
| Tenant ID | Your tenant ID |
| Scopes | `api://<API ID>/access_as_user` |

4. Select **Save**.
5. Select **Test connection**.
6. Sign in. Make sure that the `aud` claim of the token is the downstream API.

CAUTION: Do not leave the **Token Exchange URL** empty. The Microsoft setup procedure for bot SSO has this setting.[^bot-oauth] If it is empty, the token service does not return a `tokenExchangeResource`. Then the bot uses `Sso:BotAppIdUri` and writes a message to the log.

The bot uses this connection to make the `loginRequest` and for the sign-in fallback. If `Sso:TokenStrategy` is `TokenService`, the token service also uses it for the OBO request.

You can also use the Azure CLI for **Azure Active Directory v2**. The parameter names `tenantID` and `tokenExchangeUrl` come from a Microsoft sample template.[^sample-bicep] Before you use this command, run `az bot authsetting list-providers --provider-name Aadv2` to see the names.

```bash
az bot authsetting create -g rg-teams-sso-poc -n <unique-bot-name> -c sso --service Aadv2 \
  --client-id <SSO app ID> --client-secret '<secret>' \
  --provider-scope-string "api://<API ID>/access_as_user" \
  --parameters tenantID=<tenant ID> tokenExchangeUrl=api://botid-<SSO app ID>
```

## 6 Host the bot in Azure

1. Make an Azure host for the bot, for example an App Service web app or a container app.
2. Assign the managed identity from step 2 to the host.[^sdk-uami]
3. Set the application settings in this table.
4. Deploy `src/Bot` to the host.
5. Make sure that the messaging endpoint of the Azure Bot (step 4) is `https://<bot host>/api/messages`.

```bash
az webapp identity assign --name <app name> --resource-group rg-teams-sso-poc --identities <managed identity resource ID>
```

In application settings, a double underscore (`__`) replaces each colon (`:`) of a setting name.[^aspnet-env]

| Setting | Value |
| --- | --- |
| `AzureAd__TenantId` | Your tenant ID |
| `AzureAd__ClientId` | The bot ID. Do not set `ClientCredentials` in `AzureAd`. Then the Teams SDK uses the managed identity.[^sdk-uami-config] |
| `SsoApp__ClientId` | The SSO app ID |
| `SsoApp__ClientCredentials__0__SourceType` | `SignedAssertionFromManagedIdentity` |
| `SsoApp__ClientCredentials__0__ManagedIdentityClientId` | The bot ID |
| `DownstreamApi__Url` | The URL of the downstream API, for example `https://<API host>/api/me` |
| `DownstreamApi__Scopes__0` | `api://<API ID>/access_as_user` |

None of these settings is a secret. The setting names in `SsoApp:ClientCredentials` are the same as in Microsoft.Identity.Web.[^fic-miw]

## 7 Optional: run the bot on your computer

The managed identity is not available on your computer.[^mi-overview] For this reason, you must have a second Azure Bot that has a client secret.

1. Do step 3.6.
2. Make a second Azure Bot. Set **Type of App** to **Single Tenant**. Set the **App ID** to the SSO app ID.[^bot-types]
3. Set its messaging endpoint to `https://<public host>/api/messages`.[^bot-endpoint]
4. Add the Microsoft Teams channel.
5. Add an OAuth connection named `sso` to the second Azure Bot, as in step 5.
6. Start a tunnel to port 3978. Use one of these commands:[^dev-tunnels]

   ```bash
   devtunnel host -p 3978 --allow-anonymous
   ngrok http 3978
   ```

7. Start the downstream API. It listens on port 5000.
8. Start the bot. It listens on port 3978.

```bash
dotnet run --project src/DownstreamApi
dotnet run --project src/Bot
```

The bot log shows the identity that the bot uses. On your computer, the log line "Bot ready" shows "SSO app: \<SSO app ID\> from AzureAd with ClientSecret". In Azure, it shows "from SsoApp with federated credential from managed identity \<bot ID\>".

## 8 Install the Teams app

1. Make the app package. Use the bot ID and the SSO app ID:

   ```bash
   dotnet run scripts/package-app.cs -- <bot ID> <SSO app ID>
   ```

   The command writes `appPackage/build/appPackage.zip`. It sets `bots[0].botId` to the bot ID and `webApplicationInfo` from the SSO app ID:[^bot-manifest]

   ```json
   "webApplicationInfo": { "id": "<SSO app ID>", "resource": "api://botid-<SSO app ID>" }
   ```

   For the second Azure Bot of step 7, make a different package with `dotnet run scripts/package-app.cs -- <SSO app ID>`.

2. In Teams, go to **Apps**, then **Manage your apps**.
3. Select **Upload an app**, then **Upload a custom app**.
4. Select `appPackage.zip`.
5. Add the app for yourself, in the personal scope.
6. Send a message to the bot.
7. On the card, select **Call downstream API**.

NOTE: Your tenant must permit the upload of custom apps.[^apps-upload]

## 9 Make sure that there is no prompt

1. Select **Call downstream API** on the card.
2. Make sure that the card shows the result and that no dialog opens.
3. Make sure that the bot log has no line with "falling back to sign-in".

If a consent dialog ("View and accept") opens, do these checks:

1. Make sure that an administrator gave admin consent (step 3.4).
2. Make sure that the two Teams client IDs have pre-authorization (step 3.1).

If the card shows a **Sign in** button or an error, read [troubleshooting.md](troubleshooting.md). The bot log tells you the cause.

[^dev-tunnels]: [Dev tunnels overview](https://learn.microsoft.com/azure/developer/dev-tunnels/overview) †.
[^apps-upload]: [Upload your custom app: Upload your app](https://learn.microsoft.com/microsoftteams/platform/concepts/deploy-and-publish/apps-upload#upload-your-app).
[^expose-api]: [Configure an application to expose a web API: Add a scope requiring admin and user consent](https://learn.microsoft.com/entra/identity-platform/quickstart-configure-app-expose-web-apis#add-a-scope-requiring-admin-and-user-consent). A pre-authorized client app does not cause a consent prompt.
[^manifest-version]: [Microsoft Entra app manifest: requestedAccessTokenVersion attribute](https://learn.microsoft.com/entra/identity-platform/reference-app-manifest#requestedaccesstokenversion-attribute). "If the value is null, this parameter defaults to 1".
[^manifest-legacy]: [Azure AD Graph app manifest deprecation: Attribute differences](https://learn.microsoft.com/entra/identity-platform/azure-active-directory-graph-app-manifest-deprecation#attribute-differences-between-azure-ad-graph-and-microsoft-graph-formats). `accessTokenAcceptedVersion` became `requestedAccessTokenVersion`.
[^user-secrets]: [Safe storage of app secrets in development in ASP.NET Core](https://learn.microsoft.com/aspnet/core/security/app-secrets) †.
[^multitenant]: [Manage your apps in Developer Portal: Use tools to create app features](https://learn.microsoft.com/microsoftteams/platform/concepts/build-and-test/manage-your-apps-in-developer-portal#use-tools-to-create-app-features). "Bots configured as multi tenant in Azure Bot Service are deprecated."
[^bot-secret]: [Configure your bot app in Microsoft Entra ID: To create a client secret for your app](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-register-aad?tabs=botid#to-create-a-client-secret-for-your-app).
[^bot-uri]: [Configure your bot app in Microsoft Entra ID: To configure application ID URI](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-register-aad?tabs=botid#to-configure-application-id-uri). For a standalone bot, use `api://botid-{YourBotId}`.
[^bot-scope]: [Configure your bot app in Microsoft Entra ID: To configure API scope](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-register-aad?tabs=botid#to-configure-api-scope).
[^bot-clients]: [Configure your bot app in Microsoft Entra ID: To configure authorized client application](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-register-aad?tabs=botid#to-configure-authorized-client-application). Lists the Teams client IDs.
[^bot-version]: [Configure your bot app in Microsoft Entra ID: To define the access token version](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-register-aad?tabs=botid#to-define-the-access-token-version).
[^redirect]: [Add authentication to your Teams bot: Create the identity provider](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/add-authentication#create-the-identity-provider). Gives the redirect URL `https://token.botframework.com/.auth/web/redirect`.
[^bot-redirect]: [Configure your bot app in Microsoft Entra ID: To configure redirect URL](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-register-aad?tabs=botid#to-configure-redirect-url). Refers to the OAuth URLs for data residency.
[^grant-consent]: [Grant tenant-wide admin consent to an application: Grant admin consent in App registrations pane](https://learn.microsoft.com/entra/identity/enterprise-apps/grant-admin-consent#grant-admin-consent-in-app-registrations-pane).
[^obo-admin]: [Microsoft identity platform and OAuth 2.0 On-Behalf-Of flow: Admin consent](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow#admin-consent).
[^errors]: [Microsoft Entra authentication and authorization error codes: AADSTS error codes](https://learn.microsoft.com/entra/identity-platform/reference-error-codes#aadsts-error-codes). `AADSTS65001`: "The user or administrator hasn't consented to use the application".
[^bot-endpoint]: [Configure your bot app in Microsoft Entra ID: To configure messaging endpoint for your bot resource](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-register-aad?tabs=botid#to-configure-messaging-endpoint-for-your-bot-resource).
[^bot-oauth]: [Configure your bot app in Microsoft Entra ID: To update OAuth connection](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-register-aad?tabs=botid#to-update-oauth-connection). Gives the client ID, secret, Token Exchange URL, tenant ID, scopes and the Test connection step.
[^aadv2-connection]: [Add authentication to your Teams bot: Microsoft Azure Active Directory (Azure AD) v2](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/add-authentication#microsoft-azure-active-directory-azure-ad-v2).
[^sample-bicep]: [Microsoft Teams Samples: bot-teams-authentication, azure.bicep, lines 47 to 73](https://github.com/OfficeDev/Microsoft-Teams-Samples/blob/5bff3363f7e31a1d7f02e60589e0d6c1c9d88102/samples/TeamsSDK/Archived/bot-teams-authentication/csharp/M365Agent/infra/azure.bicep#L47-L73). The OAuth connection uses the keys `clientId`, `clientSecret`, `tenantID` and `tokenExchangeUrl`.
[^bot-manifest]: [Update app manifest for SSO: webApplicationInfo property](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-manifest#webapplicationinfo-property).
[^mi-overview]: [What is managed identities for Azure resources?: What are managed identities?](https://learn.microsoft.com/entra/identity/managed-identities-azure-resources/overview#what-are-managed-identities). "A managed identity is an identity that can be assigned to an Azure compute resource". "Managed identity replaces secrets".
[^mi-fic]: [What is managed identities for Azure resources?: Use managed identity as a Federated Identity Credential (FIC) on an Entra ID app](https://learn.microsoft.com/entra/identity/managed-identities-azure-resources/overview#use-managed-identity-as-a-federated-identity-credential-fic-on-an-entra-id-app). "Whenever an Entra ID app is required, this is the recommended way to be credential-free."
[^fic-values]: [Configure an application to trust a managed identity: Important considerations and restrictions](https://learn.microsoft.com/entra/workload-id/workload-identity-federation-config-app-trust-managed-identity#important-considerations-and-restrictions). Gives the issuer, the subject (the object (principal) ID), the audiences of each cloud and the same-tenant rule. Incorrect values show only at the token exchange.
[^fic-create]: [Configure an application to trust a managed identity: Configure a federated identity credential on an application](https://learn.microsoft.com/entra/workload-id/workload-identity-federation-config-app-trust-managed-identity#configure-a-federated-identity-credential-on-an-application). Gives the **Managed Identity** scenario in the admin center, the Azure CLI command and the JSON file. "You can only use User-Assigned Managed Identities as a credential."
[^fic-miw]: [Configure an application to trust a managed identity: Microsoft.Identity.Web](https://learn.microsoft.com/entra/workload-id/workload-identity-federation-config-app-trust-managed-identity#microsoftidentityweb). `SourceType` `SignedAssertionFromManagedIdentity` with `ManagedIdentityClientId`.
[^fic-propagate]: [Important considerations and restrictions for federated identity credentials: Time for federated credential changes to propagate](https://learn.microsoft.com/entra/workload-id/workload-identity-federation-considerations#time-for-federated-credential-changes-to-propagate). "AADSTS70021: No matching federated identity record found for presented assertion."
[^uami-sso]: [Enable SSO for your bot app: Enable SSO for a Teams app](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-overview#enable-sso-for-a-teams-app). "If your bot is created using User-Assigned Managed Identity, an App Registration isn't automatically created. You must create a separate App Registration".
[^uami-manifest]: [Update app manifest for SSO: webApplicationInfo property](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-manifest#webapplicationinfo-property). With the managed identity ID in `webApplicationInfo`, "app installation fails".
[^separate-app]: [Configure your bot app in Microsoft Entra ID: To configure authorized client application](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-register-aad?tabs=botid#to-configure-authorized-client-application). A `webApplicationInfo` ID that is not the Azure Bot app ID "is a valid and supported configuration".
[^bot-types]: [Add authentication to your Teams bot: Create Azure Bot resource registration](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/add-authentication#create-azure-bot-resource-registration). "All new bot registrations must either use the Single Tenant bot type or the User-Assigned Managed Identity type."
[^sdk-uami]: [Teams SDK documentation source: User Assigned Managed Identity Setup, lines 25 to 33](https://github.com/microsoft/teams-sdk/blob/0187ab0b2c43a9f21e9398a593835802d62fbecb/teams.md/docs/main/teams/app-authentication/user-managed-identity.md#L25-L33). Set **Type of App** to User Assigned Managed Identity. Assign the identity to the service that runs the bot.
[^sdk-uami-config]: [Teams SDK documentation source: C# app authentication, line 94](https://github.com/microsoft/teams-sdk/blob/0187ab0b2c43a9f21e9398a593835802d62fbecb/teams.md/src/components/include/essentials/app-authentication/csharp.incl.md#L94). "automatically use User Assigned Managed Identity authentication when you provide the `ClientId` without a `ClientSecret`".
[^azcli-identity]: [Azure CLI source: identity commands.py, lines 21 to 23](https://github.com/Azure/azure-cli/blob/3efe60f58acc8e2b397c0bf9b34fe57a7618b387/src/azure-cli/azure/cli/command_modules/identity/commands.py#L21-L23). The `az identity` group has the `create` command.
[^azcli-bot]: [Azure CLI source: botservice _params.py, lines 60 to 63](https://github.com/Azure/azure-cli/blob/3efe60f58acc8e2b397c0bf9b34fe57a7618b387/src/azure-cli/azure/cli/command_modules/botservice/_params.py#L60-L63). `az bot create` has `--appid`, `--app-type` (`UserAssignedMSI`), `--tenant-id` and `--msi-resource-id`.
[^bot-fic]: [Implement authentication with federated identity credentials](https://learn.microsoft.com/azure/bot-service/bot-builder-authentication-federated-credential) †. The OAuth connection service provider **AAD v2 with Federated Credentials**.
[^agents-fic]: [Add user authorization using federated identity credential](https://learn.microsoft.com/microsoft-365/agents-sdk/azure-bot-user-authorization-federated-credentials) †. The subject of the federated credential and the unique identifier of the OAuth connection.
[^aspnet-env]: [Configuration in ASP.NET Core: Configuration keys and values](https://learn.microsoft.com/aspnet/core/fundamentals/configuration/#configuration-keys-and-values). "A double underscore (`__`) is supported by all platforms and is automatically converted into a colon".
