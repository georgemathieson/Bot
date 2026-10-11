# How the on-behalf-of (OBO) flow works

This document uses ASD-STE100 Simplified Technical English. Section 15 gives the rules that it applies.

## 1 Purpose

This document tells you how the bot gets a token for the downstream API. The token is for the user who clicks the card button. The user does not see a sign-in window or an MFA prompt.

Read this document before you change the code in `src/Bot/Sso/`.

## 2 Summary

The bot cannot get a user token directly from Microsoft Entra ID. Only the Teams client has a session for the user. The flow has two parts:

1. The bot asks the Teams client for a token. The Teams client gets this token from Entra ID without a prompt. This token is the SSO token. The SSO token is for the bot only.
2. The bot sends the SSO token to Entra ID and asks for a token for the downstream API. This step is the on-behalf-of (OBO) flow. Entra ID returns the OBO token.

The OBO token is for the downstream API. It identifies the same user as the SSO token. The bot sends the OBO token to the downstream API in the `Authorization` header.

```mermaid
flowchart LR
    U(["User"]) -- "1 Clicks the card button" --> T["Teams client"]
    T -- "2 Invoke activity" --> B["Bot"]
    B -- "3 Asks for an SSO token<br/>(401 loginRequest)" --> T
    T -- "4 Gets the SSO token<br/>without a prompt" --> E[("Microsoft Entra ID")]
    T -- "5 Same invoke activity<br/>with the SSO token" --> B
    B -- "6 OBO request<br/>with the SSO token" --> E
    E -- "7 OBO token" --> B
    B -- "8 Request with the OBO token" --> A["Downstream API"]
```

## 3 Terms

| Term | Description |
| --- | --- |
| Access token | A JSON Web Token (JWT) that Entra ID signs. An API accepts the token as the identity of the caller and the user. |
| Audience (`aud` claim) | The API that the token is for. An API must reject a token that has a different audience. |
| Scope (`scp` claim) | The delegated permission in the token, for example `access_as_user`. Only a user token has this claim. |
| Authorized party (`azp` claim, `appid` in v1 tokens) | The app that asked Entra ID for the token. |
| Object ID (`oid` claim) | The ID of the user in Entra ID. |
| SSO token | The access token that the Teams client gets for the bot. The audience is the bot. The authorized party is the Teams client. |
| OBO token | The access token that the bot gets for the downstream API. The audience is the downstream API. The authorized party is the bot. |
| Service token | The token in the `Authorization` header of each request from the Bot Connector to the bot. It identifies the Bot Connector. It does not identify the user. |
| Assertion | The token that the bot sends in the OBO request. In this flow, the assertion is the SSO token. |
| Pre-authorization | A setting in the bot app registration. It lets the Teams client get the SSO token without a consent prompt. |
| Admin consent | A permission that an administrator gives for all users in the tenant. It lets the bot get the OBO token without a consent prompt. |
| OAuth connection | A setting on the Azure Bot resource. The Bot Framework token service uses it for the sign-in link and for the token exchange. |
| Magic code | A one-time code. The Teams client sends it to the bot after the user signs in with the Sign in button. |

## 4 Parts of the system

Seven parts take part in the flow. Microsoft operates the Teams client, the Bot Connector, the Bot Framework token service and Entra ID. You operate the bot and the downstream API.

```mermaid
flowchart TB
    U(["User"])
    subgraph MS["Microsoft cloud"]
        T["Teams client"]
        BC["Bot Connector"]
        TS["Bot Framework<br/>token service"]
        E[("Microsoft Entra ID")]
    end
    subgraph YOU["Your Azure subscription"]
        B["Bot<br/>src/Bot"]
        A["Downstream API<br/>src/DownstreamApi"]
    end
    U --> T
    T <-- "Invoke activity and response" --> BC
    BC <-- "POST /api/messages<br/>(service token)" --> B
    B -- "Sign-in link and<br/>tokenExchangeResource" --> TS
    T -- "SSO token request" --> E
    B -- "OBO request" --> E
    B -- "Request with the OBO token" --> A
```

Three app identities take part in the flow:

| App | Client ID | Function in the flow |
| --- | --- | --- |
| Teams client | `1fec8e78-bce4-4aaf-ab1b-5451cc387264` (desktop and mobile), `5e3ce6c0-2b1f-4285-8d4b-75ee78787346` (web) | Microsoft owns these apps. The Teams client asks Entra ID for the SSO token. |
| Bot app registration | Your bot ID | It exposes the scope `api://botid-<bot ID>/access_as_user`. The bot uses its client secret to ask for the OBO token. |
| Downstream API app registration | Your API ID | It exposes the scope `api://<API ID>/access_as_user`. The downstream API accepts the OBO token. |

Two permissions connect the three apps. Each permission removes one consent prompt.

```mermaid
flowchart LR
    TC["Teams client<br/>1fec8e78-… and 5e3ce6c0-…"]
    BOT["Bot app registration<br/>api://botid-‹bot ID›"]
    API["Downstream API app registration<br/>api://‹API ID›"]
    TC -- "Pre-authorization for<br/>access_as_user" --> BOT
    BOT -- "Delegated permission<br/>access_as_user<br/>+ admin consent" --> API
```

## 5 The three tokens

Each request from the Bot Connector to the bot has a service token. This token identifies the Bot Connector. It does not identify the user, and it has no `scp` claim. Entra ID rejects it as an OBO assertion.

The user name in a request comes from `activity.from`. This field is not a token. The bot cannot use it to get a token.

The bot must get the SSO token from the Teams client. Then the bot uses the SSO token to get the OBO token.

```mermaid
flowchart LR
    subgraph S["Service token: not usable for OBO"]
        s1["iss: api.botframework.com<br/>aud: ‹bot ID›<br/>no user claims"]
    end
    subgraph A["SSO token"]
        a1["aud: ‹bot ID›<br/>azp: Teams client<br/>scp: access_as_user<br/>oid: the user"]
    end
    subgraph B["OBO token"]
        b1["aud: ‹API ID›<br/>azp: ‹bot ID›<br/>scp: access_as_user<br/>oid: the same user"]
    end
    A -- "OBO request<br/>to Entra ID" --> B
    B -- "Authorization: Bearer" --> API["Downstream API"]
```

| Claim | Service token | SSO token | OBO token |
| --- | --- | --- | --- |
| `iss` | `https://api.botframework.com` | Entra ID, your tenant | Entra ID, your tenant |
| `aud` | Bot ID | Bot ID (v2) or `api://botid-<bot ID>` (v1) | API ID (v2) or `api://<API ID>` (v1) |
| `azp` or `appid` | None | Teams client ID | Bot ID |
| `scp` | None | `access_as_user` | `access_as_user` |
| `oid`, `tid`, `name` | None | The user | The same user |
| Sender and receiver | Bot Connector to bot | Teams client to bot | Bot to downstream API |

CAUTION: Do not send the SSO token to the downstream API. The audience of the SSO token is the bot, so the API must reject it.

## 6 The full sequence

This sequence shows the silent path. The user clicks once and sees the result. Steps 4 to 9 occur without a prompt.

```mermaid
sequenceDiagram
    autonumber
    actor U as User
    participant T as Teams client
    participant BC as Bot Connector
    participant B as Bot
    participant TS as BF token service
    participant E as Entra ID
    participant A as Downstream API

    U->>T: Clicks "Call downstream API" (Action.Execute)
    T->>BC: adaptiveCard/action invoke
    BC->>B: POST /api/messages (service token, no user token)
    B->>TS: GetSignInResource (OAuth connection)
    TS-->>B: Sign-in link + tokenExchangeResource
    B-->>T: HTTP 200, body statusCode 401, loginRequest
    T->>E: Token request for api://botid-‹bot ID›
    E-->>T: SSO token
    T->>B: Same invoke + value.authentication.token
    B->>B: Validate the SSO token
    B->>E: OBO request (assertion = SSO token)
    E-->>B: OBO token
    B->>A: GET /api/me (Bearer OBO token)
    A->>A: Validate the OBO token
    A-->>B: 200 with user data
    B-->>T: HTTP 200, body statusCode 200, new card
```

| Step | Description | Code |
| --- | --- | --- |
| 1 | The user clicks the `Action.Execute` button on the card. | `Cards.Welcome` |
| 2 to 3 | Teams sends an `adaptiveCard/action` invoke activity. The Bot Connector sends it to the bot with a service token. The Teams SDK validates the service token. | `Program.cs`, `OnAdaptiveCardAction` |
| 4 to 5 | The bot asks the token service for the sign-in resource of the OAuth connection. The response has a sign-in link and a `tokenExchangeResource`. | `TokenServiceClient.GetSignInResourceAsync` |
| 6 | The bot replies with a `loginRequest`. The HTTP status is 200. The body has `statusCode` 401 and the `tokenExchangeResource`. The Bot Connector sends the reply to the Teams client. | `CardActionSso`, `CardActionResponses.LoginRequest` |
| 7 to 8 | The Teams client asks Entra ID for a token for `api://botid-<bot ID>`. It uses the current session of the user. Entra ID returns the SSO token. | Teams client |
| 9 | The Teams client sends the same invoke activity again, through the Bot Connector. The SSO token is in `value.authentication.token`. | `CardActionTurn.FromContext` |
| 10 | The bot validates the SSO token. Section 12 gives the checks. | `SsoTokenValidator` |
| 11 to 12 | The bot sends the OBO request to Entra ID. Entra ID returns the OBO token. Section 7 gives the details. | `OboTokenStrategy`, `MsalOboTokenAcquirer` |
| 13 to 15 | The bot sends a request to the downstream API with the OBO token. The API validates the token and returns the user data. | `DownstreamApiClient`, `src/DownstreamApi/Program.cs` |
| 16 | The bot returns a new card. The HTTP status is 200, and the body has `statusCode` 200. | `Cards.Result` |

NOTE: The bot does steps 4 to 9 on each click when `Sso:TokenStrategy` is `Obo`. Steps 7 and 8 usually do not go to Entra ID. The Teams client has the SSO token in its cache.

## 7 The OBO request

The bot uses MSAL.NET for the OBO request. MSAL.NET sends this HTTP request to the token endpoint of Entra ID:

```http
POST https://login.microsoftonline.com/{tenant-id}/oauth2/v2.0/token
Content-Type: application/x-www-form-urlencoded

grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer
&requested_token_use=on_behalf_of
&client_id={bot-id}
&client_secret={bot-secret}
&assertion={SSO token}
&scope=api://{api-id}/access_as_user
```

| Field | Value | Function |
| --- | --- | --- |
| `grant_type` | `urn:ietf:params:oauth:grant-type:jwt-bearer` | Identifies the request as a JWT bearer grant. |
| `requested_token_use` | `on_behalf_of` | Tells Entra ID to use the OBO flow. |
| `client_id`, `client_secret` | The bot app registration | Identifies the bot. The bot can use a certificate or a managed identity instead of a secret. |
| `assertion` | The SSO token | Identifies the user. |
| `scope` | `api://<API ID>/access_as_user` | Identifies the downstream API and the delegated permission. |

Entra ID examines the request before it returns a token:

```mermaid
sequenceDiagram
    participant B as Bot (MSAL.NET)
    participant E as Entra ID token endpoint
    B->>E: POST /oauth2/v2.0/token (jwt-bearer, assertion = SSO token)
    Note over E: 1 Signature and issuer of the assertion
    Note over E: 2 Expiry time of the assertion
    Note over E: 3 Audience of the assertion = the bot?
    Note over E: 4 Client credential of the bot
    Note over E: 5 Consent: bot to downstream API, for this user
    Note over E: 6 Conditional Access policies of the downstream API
    alt All checks are correct
        E-->>B: 200 access_token = OBO token (aud = API, azp = bot)
    else Consent or MFA is necessary
        E-->>B: 400 invalid_grant, interaction_required or AADSTS65001
    else Configuration is incorrect
        E-->>B: 400 or 401, for example AADSTS50013 or AADSTS7000215
    end
```

1. Entra ID examines the signature and the issuer of the assertion. Entra ID must be the issuer.
2. Entra ID examines the expiry time of the assertion. If the expiry time is in the past, Entra ID returns `AADSTS500133`.
3. Entra ID examines the audience of the assertion. The audience must be the app that sends the request. If it is not, Entra ID returns `AADSTS50013`.
4. Entra ID examines the client credential of the bot. If the secret is incorrect, Entra ID returns `AADSTS7000215`.
5. Entra ID examines the consent for the bot to use the downstream API for this user. If there is no consent, Entra ID returns `AADSTS65001`.
6. Entra ID applies the Conditional Access policies of the downstream API. A policy can ask for a control that the user session does not have, for example MFA. Then Entra ID returns `interaction_required` and a claims challenge.

The OBO token has the same user claims as the SSO token: `oid`, `tid` and `name`. Entra ID also keeps the authentication details of the user session, for example MFA.

This code in `src/Bot/Sso/DownstreamTokenStrategies.cs` sends the request:

```csharp
var result = await _msal.AcquireTokenOnBehalfOf(scopes, new UserAssertion(assertion))
    .ExecuteAsync(cancellationToken);
return result.AccessToken;
```

MSAL.NET keeps the OBO token in a memory cache. The cache key is a hash of the assertion. If the Teams client sends the same SSO token again, MSAL.NET returns the cached OBO token. It does not send a new request to Entra ID.

## 8 Why the user sees no prompt

The flow has three places where a prompt can occur. Each place has a condition that removes the prompt.

```mermaid
flowchart TD
    S1{"Is the Teams client<br/>pre-authorized for the bot scope?"}
    S1 -- "No" --> P1["Teams shows a consent dialog"]
    S1 -- "Yes" --> S2{"Did an administrator give consent<br/>for the downstream API scope?"}
    S2 -- "No" --> P2["OBO returns AADSTS65001<br/>Teams shows the Sign in button"]
    S2 -- "Yes" --> S3{"Does the Teams session agree with<br/>the Conditional Access policies of the API?"}
    S3 -- "No" --> P3["OBO returns interaction_required<br/>Teams shows the Sign in button"]
    S3 -- "Yes" --> OK(["No prompt: the card shows the result"])
```

Make sure that these conditions are true:

1. The bot app registration exposes the scope `access_as_user` at the Application ID URI `api://botid-<bot ID>`.
2. The Teams client IDs `1fec8e78-bce4-4aaf-ab1b-5451cc387264` and `5e3ce6c0-2b1f-4285-8d4b-75ee78787346` have pre-authorization for this scope.
3. In the Teams manifest, `webApplicationInfo.id` is the bot ID and `webApplicationInfo.resource` is the Application ID URI.
4. The bot app registration has the delegated permission `access_as_user` of the downstream API.
5. An administrator gave admin consent for the bot app registration.
6. The Token Exchange URL of the OAuth connection is the Application ID URI.
7. The user installed the app in the personal scope.

The Teams client gets the SSO token with the current session of the user. If the user did MFA to sign in to Teams, the session has this MFA. Entra ID keeps it in the OBO flow, so the OBO flow does not ask for MFA again.

## 9 Decision logic in the bot

`CardActionSso.HandleAsync` examines each invoke activity in this sequence:

```mermaid
flowchart TD
    IN(["adaptiveCard/action invoke"]) --> Q1{"Is there a magic code<br/>in value.state?"}
    Q1 -- "Yes" --> R1["Send the magic code to the token service"]
    R1 --> Q2{"Did the token service<br/>return a token?"}
    Q2 -- "Yes" --> H1["Run the handler<br/>source: SignInFallback"]
    Q2 -- "No" --> L2["401 loginRequest<br/>without tokenExchangeResource"]
    Q1 -- "No" --> Q3{"Is there an SSO token in<br/>value.authentication.token?"}
    Q3 -- "Yes" --> V["Validate the SSO token"]
    V -- "Not valid" --> F412["412 preconditionFailed"]
    V -- "Valid" --> O["Get the OBO token"]
    O -- "Consent or MFA<br/>is necessary" --> L2
    O -- "Other error" --> ERR["Error card"]
    O -- "OBO token" --> H2["Run the handler<br/>source: SsoThenObo"]
    Q3 -- "No" --> Q4{"Is there a cached token?<br/>(TokenService strategy only)"}
    Q4 -- "Yes" --> H3["Run the handler<br/>source: TokenServiceCache"]
    Q4 -- "No" --> L1["401 loginRequest<br/>with tokenExchangeResource"]
```

All responses use HTTP status 200. The Teams client reads the `statusCode` in the response body.

## 10 The fallback path

If Entra ID cannot give the OBO token without an action from the user, the bot sends a different reply. It is a `loginRequest` without a `tokenExchangeResource`. Then Teams shows a Sign in button on the card. The user sees this button only one time. After the user signs in, the consent and the MFA status are in place for the next clicks.

```mermaid
sequenceDiagram
    autonumber
    actor U as User
    participant T as Teams client
    participant B as Bot
    participant TS as BF token service
    participant E as Entra ID

    B-->>T: 401 loginRequest without tokenExchangeResource
    T->>U: Shows the Sign in button on the card
    U->>T: Selects Sign in
    T->>E: Opens the sign-in page of the OAuth connection
    U->>E: Signs in, gives consent, completes MFA
    E-->>TS: Authorization code
    TS->>E: Token request with the OAuth connection credentials
    E-->>TS: Token for the downstream API
    TS-->>T: Magic code
    T->>B: Same invoke + value.state (magic code)
    B->>TS: GetToken with the magic code
    TS-->>B: Token for the downstream API
    Note over B: The bot runs the handler (source: SignInFallback)
```

The Scopes setting of the OAuth connection must be the scope of the downstream API. Then the sign-in gives consent for the downstream API. It also gives a token that the bot can send to the API directly.

## 11 Failure conditions

| Condition | Bot response | Result in Teams |
| --- | --- | --- |
| The SSO token is not valid: signature, issuer, audience, expiry, tenant, scope or user | 412 `preconditionFailed` | Sign in button |
| There is no consent for the downstream API (`AADSTS65001`) | 401 `loginRequest` without `tokenExchangeResource` | Sign in button |
| A Conditional Access policy asks for MFA (`AADSTS50076`, `interaction_required`) | 401 `loginRequest` without `tokenExchangeResource` | Sign in button |
| The configuration is incorrect (`AADSTS7000215`, `AADSTS70011`, `AADSTS50013`) | 200 with an error card | Error card with the AADSTS code, if `Sso:ShowDiagnostics` is `true` |
| The magic code is not correct or is too old | 401 `loginRequest` without `tokenExchangeResource` | Sign in button |
| The downstream API rejects the OBO token (401 or 403) | 200 with an error card | Error card |
| The card action has an unknown verb | 400 error | Teams error message |

## 12 Security controls

1. The Teams SDK validates the service token on each request to `/api/messages`. Only the Bot Connector can send activities to the bot.
2. The bot validates the SSO token before it uses the token. It examines the signature, the issuer, the audience, the expiry, the tenant and the scope.
3. The bot makes sure that the `oid` claim of the SSO token is the same as `activity.from.aadObjectId`. This check prevents the use of a token in the activity of a different user.
4. If Entra ID changes its signing keys, the bot gets the new keys one time and validates the token again.
5. The downstream API accepts only tokens for its own audience. It rejects the SSO token. It also rejects app-only tokens, because they have no `scp` claim.
6. The bot keeps tokens in memory only. It does not write tokens to the logs or to the cards.

WARNING: Do not set the log level of `Microsoft.Teams` to `Trace` in a shared or production environment. At this level, the Teams SDK writes each invoke activity to the log. The SSO token is in the invoke activity.

CAUTION: Do not use a client secret in production. Use a certificate or a managed identity with a federated credential. A secret can leak, and it expires.

## 13 The two strategies

The setting `Sso:TokenStrategy` selects which component sends the OBO request to Entra ID.

```mermaid
flowchart LR
    subgraph O["Obo (default)"]
        B1["Bot"] -- "OBO request<br/>(MSAL.NET)" --> E1[("Entra ID")]
    end
    subgraph T["TokenService"]
        B2["Bot"] -- "Token exchange request" --> TS["BF token service"]
        TS -- "OBO request<br/>(OAuth connection)" --> E2[("Entra ID")]
    end
```

| Item | `Obo` | `TokenService` |
| --- | --- | --- |
| Component that sends the OBO request | The bot, with MSAL.NET | The Bot Framework token service, with the client ID, secret and scopes of the OAuth connection |
| Token storage | MSAL.NET memory cache only. Each click starts the SSO steps again. | The token service keeps the token and refreshes it. Later clicks do not start the SSO steps. |
| Number of downstream APIs | Many. The bot can use one SSO token for many APIs. | One API for each OAuth connection |
| Conditional Access claims challenge | The code gets it in `InteractionRequiredException.Claims` | The token service does not give it to the code |

## 14 Code map

| Function | File |
| --- | --- |
| Routes the invoke activity to the SSO logic | `src/Bot/Program.cs` |
| Reads the invoke value and `value.authentication` | `src/Bot/Sso/CardActionTurn.cs` |
| Runs the decision logic of section 9 | `src/Bot/Sso/CardActionSso.cs` |
| Builds the `loginRequest`, 412, card and error responses | `src/Bot/Sso/CardActionResponses.cs` |
| Validates the SSO token | `src/Bot/Sso/SsoTokenValidator.cs` |
| Sends the OBO request, or uses the token service | `src/Bot/Sso/DownstreamTokenStrategies.cs` |
| Calls the Bot Framework token service | `src/Bot/Sso/TokenServiceClient.cs` |
| Validates the OBO token in the downstream API | `src/DownstreamApi/Program.cs` |
| Tests each step with a test tenant | `test/Bot.Tests/EndToEndTests.cs` |

## 15 Writing standard

This document uses the rules of ASD-STE100 Simplified Technical English:

- Each sentence has one topic. Descriptive sentences have 25 words or fewer. Instructions have 20 words or fewer.
- Each paragraph has six sentences or fewer.
- Verbs are in the active voice and in a simple tense. Instructions use the imperative.
- Warnings and cautions start with a command, then give the reason.
- The document uses technical names and technical verbs from Microsoft Teams, Microsoft Entra ID, OAuth 2.0 and .NET. Examples are "token", "scope", "consent", "validate" and "sign in". These words are technical names or technical verbs.

A full check against the STE dictionary is still necessary.

## 16 References

- [Microsoft identity platform and OAuth 2.0 On-Behalf-Of flow](https://learn.microsoft.com/entra/identity-platform/v2-oauth2-on-behalf-of-flow)
- [Access token claims reference](https://learn.microsoft.com/entra/identity-platform/access-token-claims-reference)
- [Enable SSO for Adaptive Cards Universal Actions](https://learn.microsoft.com/microsoftteams/platform/task-modules-and-cards/cards/universal-actions-for-adaptive-cards/enable-sso-for-your-adaptive-cards-universal-action)
- [Add code to enable SSO for Adaptive Cards Universal Actions](https://learn.microsoft.com/microsoftteams/platform/task-modules-and-cards/cards/universal-actions-for-adaptive-cards/sso-adaptive-cards-universal-action)
- [Configure your bot app in Microsoft Entra ID](https://learn.microsoft.com/microsoftteams/platform/bots/how-to/authentication/bot-sso-register-aad)
