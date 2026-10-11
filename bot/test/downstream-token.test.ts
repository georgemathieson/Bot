import { AuthError, InteractionRequiredAuthError, ServerError } from '@azure/msal-node';
import { describe, expect, it, vi } from 'vitest';
import { createOboStrategy, createTokenServiceStrategy, InteractionRequiredError } from '../src/sso/index.js';
import { fakeContext, fakeTokenService } from './helpers.js';

const SCOPES = ['api://downstream/access_as_user'];

function obo(acquireTokenOnBehalfOf: (...args: never[]) => unknown) {
  return createOboStrategy({
    clientId: 'bot',
    clientSecret: 'secret',
    tenantId: 'tenant',
    scopes: SCOPES,
    msalApp: { acquireTokenOnBehalfOf: vi.fn(acquireTokenOnBehalfOf) as never },
  });
}

describe('OBO strategy (MSAL)', () => {
  it('sends the SSO token as the OBO assertion for the downstream scopes', async () => {
    const acquire = vi.fn(async () => ({ accessToken: 'downstream' }));
    const strategy = obo(acquire);

    await expect(strategy.exchangeSsoToken(fakeContext(), 'sso-token')).resolves.toEqual({
      accessToken: 'downstream',
      source: 'sso+obo',
    });
    expect(acquire).toHaveBeenCalledWith({ oboAssertion: 'sso-token', scopes: SCOPES });
  });

  it('is stateless: never returns a cached token, so every click goes through silent SSO', async () => {
    await expect(obo(async () => null).getCachedToken(fakeContext())).resolves.toBeUndefined();
  });

  it('maps MSAL InteractionRequiredAuthError (e.g. consent_required) to InteractionRequiredError, keeping claims', async () => {
    const strategy = obo(async () => {
      // (errorCode, correlationId, errorMessage, subError, timestamp, traceId, claims)
      throw new InteractionRequiredAuthError('invalid_grant', 'corr-id', 'AADSTS50076: MFA required', 'basic_action', '', '', '{"access_token":{"capolids":{}}}');
    });
    const err = await strategy.exchangeSsoToken(fakeContext(), 'sso').catch((e: unknown) => e);
    expect(err).toBeInstanceOf(InteractionRequiredError);
    expect((err as InteractionRequiredError).claims).toContain('capolids');
  });

  it('treats AADSTS65001 (consent missing) as interaction required even when MSAL reports a plain server error', async () => {
    const strategy = obo(async () => {
      // What MSAL builds from the token endpoint response: (errorCode, correlationId, errorMessage, subError, errorNo)
      throw new ServerError('invalid_grant', 'corr-id', 'Error(s): 65001 - Description: AADSTS65001: The user or administrator has not consented to use the application', '', '65001');
    });
    await expect(strategy.exchangeSsoToken(fakeContext(), 'sso')).rejects.toBeInstanceOf(InteractionRequiredError);
  });

  it('rethrows configuration errors such as a bad client secret', async () => {
    const strategy = obo(async () => {
      throw new AuthError('invalid_client', 'corr-id', 'AADSTS7000215: Invalid client secret provided.');
    });
    await expect(strategy.exchangeSsoToken(fakeContext(), 'sso')).rejects.toBeInstanceOf(AuthError);
  });
});

describe('token-service strategy', () => {
  it('returns the token stored in the Bot Framework token store', async () => {
    const tokenService = fakeTokenService({ getUserToken: vi.fn(async () => 'stored') });
    await expect(createTokenServiceStrategy(tokenService).getCachedToken(fakeContext())).resolves.toBe('stored');
  });

  it('exchanges the SSO token through the token service', async () => {
    const tokenService = fakeTokenService({ exchangeToken: vi.fn(async () => 'exchanged') });
    const ctx = fakeContext();
    await expect(createTokenServiceStrategy(tokenService).exchangeSsoToken(ctx, 'sso')).resolves.toEqual({
      accessToken: 'exchanged',
      source: 'sso+token-service',
    });
    expect(tokenService.exchangeToken).toHaveBeenCalledWith(ctx, 'sso');
  });

  it('signals interaction required when the token service cannot exchange (consent missing)', async () => {
    await expect(
      createTokenServiceStrategy(fakeTokenService()).exchangeSsoToken(fakeContext(), 'sso'),
    ).rejects.toBeInstanceOf(InteractionRequiredError);
  });
});
