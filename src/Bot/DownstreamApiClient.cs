using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using TeamsSsoBot.Sso;

namespace TeamsSsoBot;

/// <summary>Shape returned by DownstreamApi GET /api/me (swap for your real service's contract).</summary>
public sealed record DownstreamResult(string Message, DownstreamUser User, DownstreamTokenInfo Token);
public sealed record DownstreamUser(string? Name, string? Username, string? Oid, string? Tid);
public sealed record DownstreamTokenInfo(string? Ver, string? Aud, string? Scp, string? CallingAppId, DateTimeOffset? ExpiresAt);

public sealed class DownstreamApiException(int status, string body)
    : Exception($"Downstream API returned {status}: {(body.Length > 300 ? body[..300] : body)}")
{
    public int Status { get; } = status;
}

/// <summary>Calls the downstream API with the user's (OBO) access token.</summary>
public sealed class DownstreamApiClient(IHttpClientFactory httpClientFactory, IOptions<DownstreamApiOptions> options)
{
    public const string HttpClientName = "DownstreamApi";

    public async Task<DownstreamResult> GetMeAsync(string accessToken, CancellationToken cancellationToken)
    {
        var url = options.Value.Url ?? throw new InvalidOperationException("DownstreamApi:Url is not configured.");
        var http = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new DownstreamApiException((int)response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        }
        return await response.Content.ReadFromJsonAsync<DownstreamResult>(cancellationToken).ConfigureAwait(false)
            ?? throw new DownstreamApiException((int)response.StatusCode, "empty body");
    }
}
