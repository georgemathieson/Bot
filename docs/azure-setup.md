# Azure and Entra ID setup

This document uses ASD-STE100 Simplified Technical English. Each step has a footnote to its Microsoft source. A dagger (†) marks a source that only a search summary confirms.

## 0 Before you start

You must have these items:

- A Microsoft Entra ID tenant. You must have permission to make app registrations and to give admin consent.
- An Azure subscription for the Azure Bot resource.
- The .NET 10 SDK. If you use the setup script, you must also have the Azure CLI.
- A public HTTPS tunnel to your computer, for example dev tunnels[^dev-tunnels] or ngrok.
- Permission to upload custom apps in Microsoft Teams.[^apps-upload]

In this document, "bot ID" is the Application (client) ID of the bot app registration. "API ID" is the Application (client) ID of the downstream API app registration.

These values must be the same in all of their locations:

| Value | Locations |
| --- | --- |
| `api://botid-<bot ID>` | Bot app registration, Expose an API, Application ID URI. Manifest, `webApplicationInfo.resource`. OAuth connection, Token Exchange URL. Bot setting `Sso:BotAppIdUri` (default value). |
| `<bot ID>` | Bot app registration, client ID. Azure Bot, Microsoft App ID. Manifest, `bots[0].botId` and `webApplicationInfo.id`. Bot setting `AzureAd:ClientId`. |
| `api://<API ID>/access_as_user` | Downstream API app registration, scope. Bot app registration, API permissions. OAuth connection, Scopes. Bot setting `DownstreamApi:Scopes`. |

### Fast path

The script `scripts/setup-entra.sh` does steps 1 and 2, and it gives admin consent. It also keeps the settings of the two projects in `dotnet user-secrets`.

1. Sign in to the Azure CLI with `az login`.
2. Read `scripts/setup-entra.sh`.
3. Run `scripts/setup-entra.sh`.
4. Do steps 3 to 6 of this document.

CAUTION: Read the script before you run it. The script makes two new app registrations each time that it runs. The script did not run against a real tenant. It ran only against a test stub of `az`.

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

## 2 Make the bot app registration

1. Select **New registration**.
2. Type the name `poc-teams-sso-bot`.
3. Set **Supported account types** to **Accounts in this organizational directory only**.
4. Select **Register**.

NOTE: Use a single-tenant bot. Microsoft deprecated multi-tenant bots in Azure Bot Service. Bots that already exist continue to operate.[^multitenant]

### 2.1 Add a client secret

1. Go to **Certificates & secrets**.
2. Select **New client secret**.
3. Copy the secret value. You use it in step 2.6.[^bot-secret]

### 2.2 Expose the bot API for Teams SSO

1. Go to **Expose an API**.
2. Set the **Application ID URI** to `api://botid-<bot ID>`. Do not keep the default value `api://<bot ID>`.[^bot-uri]
3. Select **Add a scope**.
4. Type the scope name `access_as_user`.
5. Set **Who can consent** to **Admins and users**.
6. Set **State** to **Enabled**, then select **Add scope**.[^bot-scope]
7. Select **Add a client application**.
8. Type the client ID `1fec8e78-bce4-4aaf-ab1b-5451cc387264`. This is the Teams desktop and mobile client.
9. Select the `access_as_user` scope, then select **Add application**.
10. Do steps 7 to 9 again with the client ID `5e3ce6c0-2b1f-4285-8d4b-75ee78787346`. This is the Teams web client.

This pre-authorization lets Teams get the SSO token without a consent prompt.[^bot-clients][^expose-api]

### 2.3 Set the token version

1. Go to **Manifest**.
2. Set `requestedAccessTokenVersion` to `2`.[^bot-version]

### 2.4 Add the redirect URI for the token service

1. Go to **Authentication**.
2. Select **Add a platform**, then select **Web**.
3. Type the redirect URI `https://token.botframework.com/.auth/web/redirect`.[^redirect]
4. Select **Configure**.

The Bot Framework token service uses this URI for the sign-in fallback. If your bot uses a regional token service, use its host. An example is `europe.token.botframework.com`.[^bot-redirect]

### 2.5 Give the bot permission to use the downstream API

1. Go to **API permissions**.
2. Select **Add a permission**.
3. Select **APIs my organization uses**, then select `poc-downstream-api`.
4. Select **Delegated permissions**, then select `access_as_user`.
5. Select **Add permissions**.
6. Optional: add the Microsoft Graph delegated permissions `openid`, `profile` and `offline_access`. Then admin consent also covers the fallback sign-in.
7. Select **Grant admin consent for \<tenant\>**.[^grant-consent]

Admin consent lets the bot get the OBO token for all users without a prompt.[^obo-admin] If there is no consent, the OBO request fails with `AADSTS65001`.[^errors] Then each user sees a Sign in button one time.

### 2.6 Keep the bot settings in user-secrets

CAUTION: Do not put the client secret in `appsettings.json`. All people with access to the repository can read its files. Microsoft recommends that you keep secrets out of source code.[^user-secrets]

```bash
dotnet user-secrets set "AzureAd:TenantId" "<tenant ID>" --project src/Bot
dotnet user-secrets set "AzureAd:ClientId" "<bot ID>" --project src/Bot
dotnet user-secrets set "AzureAd:ClientCredentials:0:ClientSecret" "<secret>" --project src/Bot
dotnet user-secrets set "DownstreamApi:Scopes:0" "api://<API ID>/access_as_user" --project src/Bot
```

## 3 Make the Azure Bot resource

1. In the Azure portal, select **Create a resource**.
2. Find and select **Azure Bot**.
3. Set **Type of App** to **Single Tenant**.[^multitenant]
4. Set **Creation type** to **Use existing app registration**.
5. Set **App ID** to the bot ID, and set **App tenant ID** to your tenant ID.
6. Select **Create**.
7. Go to **Configuration** in the new resource.
8. Set **Messaging endpoint** to `https://<public host>/api/messages`.[^bot-endpoint]
9. Go to **Channels**, then add **Microsoft Teams**.

For local work, start a tunnel to port 3978. Use one of these commands:[^dev-tunnels]

```bash
devtunnel host -p 3978 --allow-anonymous
ngrok http 3978
```

You can also use the Azure CLI. This POC did not compare these commands with the Azure CLI reference. Run each command with `--help` before you use it.

```bash
az group create -n rg-teams-sso-poc -l westeurope
az bot create -g rg-teams-sso-poc -n <unique-bot-name> --app-type SingleTenant \
  --appid <bot ID> --tenant-id <tenant ID> --endpoint https://<public host>/api/messages
az bot msteams create -g rg-teams-sso-poc -n <unique-bot-name>
```

## 4 Add the OAuth connection

1. In the Azure Bot resource, go to **Configuration**.
2. Select **Add OAuth Connection Settings**.
3. Type the values in this table:[^bot-oauth][^aadv2-connection]

| Field | Value |
| --- | --- |
| Name | `sso`. This is the bot setting `Sso:OAuthConnectionName`. |
| Service Provider | **Azure Active Directory v2** |
| Client id | The bot ID |
| Client secret | The client secret from step 2.1 |
| Token Exchange URL | `api://botid-<bot ID>` |
| Tenant ID | Your tenant ID |
| Scopes | `api://<API ID>/access_as_user` |

4. Select **Save**.
5. Select **Test connection**.
6. Sign in. Make sure that the `aud` claim of the token is the downstream API.

CAUTION: Do not leave the **Token Exchange URL** empty. The Microsoft setup procedure for bot SSO has this setting.[^bot-oauth] If it is empty, the token service does not return a `tokenExchangeResource`. Then the bot uses `Sso:BotAppIdUri` and writes a message to the log.

The bot uses this connection to make the `loginRequest` and for the sign-in fallback. If `Sso:TokenStrategy` is `TokenService`, the token service also uses it for the OBO request.

You can also use the Azure CLI. The parameter names `tenantID` and `tokenExchangeUrl` come from a Microsoft sample template.[^sample-bicep] Before you use this command, run `az bot authsetting list-providers --provider-name Aadv2` to see the names.

```bash
az bot authsetting create -g rg-teams-sso-poc -n <unique-bot-name> -c sso --service Aadv2 \
  --client-id <bot ID> --client-secret '<secret>' \
  --provider-scope-string "api://<API ID>/access_as_user" \
  --parameters tenantID=<tenant ID> tokenExchangeUrl=api://botid-<bot ID>
```

## 5 Run the apps on your computer

1. Start the downstream API. It listens on port 5000.
2. Start the bot. It listens on port 3978.
3. Make sure that the tunnel from step 3 goes to port 3978.

```bash
dotnet run --project src/DownstreamApi
dotnet run --project src/Bot
```

## 6 Install the Teams app

1. Make the app package:

   ```bash
   dotnet run scripts/package-app.cs -- <bot ID>
   ```

   The command writes `appPackage/build/appPackage.zip`. It sets `webApplicationInfo` from the bot ID:[^bot-manifest]

   ```json
   "webApplicationInfo": { "id": "<bot ID>", "resource": "api://botid-<bot ID>" }
   ```

2. In Teams, go to **Apps**, then **Manage your apps**.
3. Select **Upload an app**, then **Upload a custom app**.
4. Select `appPackage.zip`.
5. Add the app for yourself, in the personal scope.
6. Send a message to the bot.
7. On the card, select **Call downstream API**.

NOTE: Your tenant must permit the upload of custom apps.[^apps-upload]

## 7 Make sure that there is no prompt

1. Select **Call downstream API** on the card.
2. Make sure that the card shows the result and that no dialog opens.
3. Make sure that the bot log has no line with "falling back to sign-in".

If a consent dialog ("View and accept") opens, do these checks:

1. Make sure that an administrator gave admin consent (step 2.5).
2. Make sure that the two Teams client IDs have pre-authorization (step 2.2).

If the card shows a **Sign in** button, read [troubleshooting.md](troubleshooting.md). The bot log tells you the cause.

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
