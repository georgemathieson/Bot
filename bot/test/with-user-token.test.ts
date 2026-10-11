import type { AdaptiveCardActionResponse } from '@microsoft/teams.api';
import { describe, expect, it, vi } from 'vitest';
import {
  InteractionRequiredError,
  TokenValidationError,
  withUserToken,
  type AuthenticatedCardActionHandler,
  type DownstreamTokenStrategy,
  type SsoTokenValidator,
} from '../src/sso/index.js';
import { BOT_APP_ID_URI, fakeContext, fakeTokenService, SIGN_IN_LINK, USER_OID } from './helpers.js';

const OK: AdaptiveCardActionResponse = { statusCode: 200, type: 'application/vnd.microsoft.activity.message', value: 'ok' };

function setup(overrides: { strategy?: Partial<DownstreamTokenStrategy>; validate?: SsoTokenValidator; tokenService?: ReturnType<typeof fakeTokenService> } = {}) {
  const tokenService = overrides.tokenService ?? fakeTokenService();
  const strategy: DownstreamTokenStrategy = {
    name: 'obo',
    getCachedToken: vi.fn(async () => undefined),
    exchangeSsoToken: vi.fn(async () => ({ accessToken: 'downstream-token', source: 'sso+obo' as const })),
    ...overrides.strategy,
  };
  const validateSsoToken = vi.fn(overrides.validate ?? (async () => ({ oid: USER_OID, aud: 'bot', scp: 'access_as_user' })));
  const handler = vi.fn<AuthenticatedCardActionHandler>(async () => OK);
  const wrapped = withUserToken(
    { connectionName: 'sso', botAppIdUri: BOT_APP_ID_URI, tokenService, strategy, validateSsoToken },
    handler,
  );
  return { wrapped, handler, strategy, tokenService, validateSsoToken };
}

describe('withUserToken (Action.Execute SSO handshake)', () => {
  it('first click: replies 401 loginRequest with a tokenExchangeResource so Teams does silent SSO', async () => {
    const { wrapped, handler } = setup();
    const res = await wrapped(fakeContext());

    expect(handler).not.toHaveBeenCalled();
    expect(res).toEqual({
      statusCode: 401,
      type: 'application/vnd.microsoft.activity.loginRequest',
      value: {
        text: 'Sign in to continue',
        connectionName: 'sso',
        tokenExchangeResource: { id: 'ter-1', uri: BOT_APP_ID_URI, providerId: 'aad-provider' },
        buttons: [{ type: 'signin', title: 'Sign in', text: 'Sign in', value: SIGN_IN_LINK }],
      },
    });
  });

  it('falls back to the configured App ID URI (and warns) if the OAuth connection has no Token Exchange URL', async () => {
    const tokenService = fakeTokenService({ getSignInResource: vi.fn(async () => ({ signInLink: SIGN_IN_LINK })) });
    const { wrapped } = setup({ tokenService });
    const ctx = fakeContext();
    const res = await wrapped(ctx);

    expect(res.statusCode).toBe(401);
    expect((res.value as { tokenExchangeResource: { uri: string } }).tokenExchangeResource.uri).toBe(BOT_APP_ID_URI);
    expect(ctx.log.warn).toHaveBeenCalledWith(expect.stringContaining('Token Exchange URL'));
  });

  it('second invoke with the SSO token: validates it, exchanges it and runs the handler', async () => {
    const { wrapped, handler, strategy, validateSsoToken } = setup();
    const ctx = fakeContext({ authentication: { id: 'ter-1', connectionName: 'sso', token: 'sso-token' } });
    const res = await wrapped(ctx);

    expect(validateSsoToken).toHaveBeenCalledWith('sso-token', USER_OID);
    expect(strategy.exchangeSsoToken).toHaveBeenCalledWith(ctx, 'sso-token');
    expect(handler).toHaveBeenCalledWith(ctx, {
      accessToken: 'downstream-token',
      source: 'sso+obo',
      ssoClaims: { oid: USER_OID, aud: 'bot', scp: 'access_as_user' },
    });
    expect(res).toBe(OK);
  });

  it('replies 412 (Teams falls back to the Sign in button) when the SSO token is invalid', async () => {
    const { wrapped, handler, strategy } = setup({
      validate: async () => {
        throw new TokenValidationError('bad audience', 401);
      },
    });
    const res = await wrapped(fakeContext({ authentication: { id: 'x', connectionName: 'sso', token: 'forged' } }));

    expect(strategy.exchangeSsoToken).not.toHaveBeenCalled();
    expect(handler).not.toHaveBeenCalled();
    expect(res).toMatchObject({ statusCode: 412, type: 'application/vnd.microsoft.error.preconditionFailed' });
  });

  it('replies with a sign-in request WITHOUT SSO info when the exchange needs consent/MFA', async () => {
    const { wrapped, handler } = setup({
      strategy: {
        exchangeSsoToken: vi.fn(async () => {
          throw new InteractionRequiredError('AADSTS65001: consent required');
        }),
      },
    });
    const res = await wrapped(fakeContext({ authentication: { id: 'x', connectionName: 'sso', token: 'sso-token' } }));

    expect(handler).not.toHaveBeenCalled();
    expect(res.statusCode).toBe(401);
    expect(res.type).toBe('application/vnd.microsoft.activity.loginRequest');
    expect((res.value as { tokenExchangeResource?: unknown }).tokenExchangeResource).toBeUndefined();
  });

  it('propagates non-interactive exchange errors (misconfiguration) to the caller', async () => {
    const boom = new Error('AADSTS7000215: Invalid client secret provided');
    const { wrapped } = setup({ strategy: { exchangeSsoToken: vi.fn(async () => Promise.reject(boom)) } });
    await expect(
      wrapped(fakeContext({ authentication: { id: 'x', connectionName: 'sso', token: 'sso-token' } })),
    ).rejects.toBe(boom);
  });

  it('after the fallback sign-in, redeems the magic code from `state`', async () => {
    const tokenService = fakeTokenService({ getUserToken: vi.fn(async () => 'token-from-sign-in') });
    const { wrapped, handler } = setup({ tokenService });
    const ctx = fakeContext({ state: '123456' });
    await wrapped(ctx);

    expect(tokenService.getUserToken).toHaveBeenCalledWith(ctx, '123456');
    expect(handler).toHaveBeenCalledWith(ctx, { accessToken: 'token-from-sign-in', source: 'sign-in-fallback' });
  });

  it('asks the user to sign in again if the magic code cannot be redeemed', async () => {
    const { wrapped, handler } = setup();
    const res = await wrapped(fakeContext({ state: 'wrong' }));

    expect(handler).not.toHaveBeenCalled();
    expect(res.statusCode).toBe(401);
    expect((res.value as { tokenExchangeResource?: unknown }).tokenExchangeResource).toBeUndefined();
  });

  it('uses an already cached token without any SSO round trip (token-service strategy)', async () => {
    const { wrapped, handler, tokenService } = setup({
      strategy: { name: 'token-service', getCachedToken: vi.fn(async () => 'cached-token') },
    });
    const ctx = fakeContext();
    await wrapped(ctx);

    expect(handler).toHaveBeenCalledWith(ctx, { accessToken: 'cached-token', source: 'token-service-cache' });
    expect(tokenService.getSignInResource).not.toHaveBeenCalled();
  });
});
