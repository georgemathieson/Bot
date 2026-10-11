using System.Text.Json.Serialization;
using Microsoft.Teams.Apps;

namespace TeamsSsoBot.Sso;

/// <summary>
/// Universal Actions invoke response bodies. Like Microsoft's C# SSO sample, these always go out as
/// HTTP 200: the status Teams acts on is the <c>statusCode</c> inside the body.
/// </summary>
public static class CardActionResponses
{
    public const string LoginRequestType = "application/vnd.microsoft.activity.loginRequest";
    public const string PreconditionFailedType = "application/vnd.microsoft.error.preconditionFailed";
    public const string ErrorType = "application/vnd.microsoft.error";
    public const string CardType = "application/vnd.microsoft.card.adaptive";

    /// <summary>
    /// 401 loginRequest. With a <paramref name="tokenExchangeResource"/>, Teams attempts silent SSO and
    /// re-sends the action with <c>value.authentication.token</c>; without one, it shows a Sign in button.
    /// </summary>
    public static InvokeResponse LoginRequest(string connectionName, string? signInLink, TokenExchangeResourceBody? tokenExchangeResource, string text = "Sign in to continue") =>
        Ok(new CardActionResponseBody(401, LoginRequestType, new OAuthCardBody(
            text,
            connectionName,
            tokenExchangeResource,
            [new SignInButton("signin", "Sign in", "Sign in", signInLink)])));

    /// <summary>412: the SSO token can't be used. Teams falls back to the Sign in button.</summary>
    public static InvokeResponse PreconditionFailed(string message) =>
        Ok(new CardActionResponseBody(412, PreconditionFailedType, new ErrorBody("412", message)));

    public static InvokeResponse Error(int statusCode, string code, string message) =>
        Ok(new CardActionResponseBody(statusCode, ErrorType, new ErrorBody(code, message)));

    /// <summary>200: replace the card with <paramref name="card"/>.</summary>
    public static InvokeResponse Card(object card) =>
        Ok(new CardActionResponseBody(200, CardType, card));

    private static InvokeResponse Ok(CardActionResponseBody body) => new(200, body);
}

public sealed record CardActionResponseBody(
    [property: JsonPropertyName("statusCode")] int StatusCode,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("value")] object? Value);

public sealed record OAuthCardBody(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("connectionName")] string ConnectionName,
    [property: JsonPropertyName("tokenExchangeResource"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] TokenExchangeResourceBody? TokenExchangeResource,
    [property: JsonPropertyName("buttons")] IReadOnlyList<SignInButton> Buttons);

/// <summary>
/// Kept as plain strings: the SDK models <c>uri</c> as <see cref="Uri"/>, whose ToString() turns
/// <c>api://botid-x</c> into <c>api://botid-x/</c>, which no longer matches the App ID URI.
/// </summary>
public sealed record TokenExchangeResourceBody(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("providerId"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProviderId);

public sealed record SignInButton(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("value")] string? Value);

public sealed record ErrorBody(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);
