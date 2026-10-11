/** Shape returned by downstream-api GET /api/me (swap for your real service's contract). */
export interface DownstreamResult {
  message: string;
  user: { name?: string; username?: string; oid?: string; tid?: string };
  token: { ver?: string; aud?: string | string[]; scp?: string; callingAppId?: string; expiresAt?: string };
}

export class DownstreamApiError extends Error {
  constructor(
    readonly status: number,
    readonly body: string,
  ) {
    super(`Downstream API returned ${status}: ${body.slice(0, 300)}`);
    this.name = 'DownstreamApiError';
  }
}

export type DownstreamApiClient = (accessToken: string) => Promise<DownstreamResult>;

export function createDownstreamApiClient(url: string, fetchImpl: typeof fetch = fetch): DownstreamApiClient {
  return async (accessToken) => {
    const res = await fetchImpl(url, {
      headers: { Authorization: `Bearer ${accessToken}`, Accept: 'application/json' },
      signal: AbortSignal.timeout(10_000),
    });
    if (!res.ok) throw new DownstreamApiError(res.status, await res.text());
    return (await res.json()) as DownstreamResult;
  };
}
