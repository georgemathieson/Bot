import type { IAdaptiveCard } from '@microsoft/teams.cards';
import type { DownstreamResult } from './downstream-api.js';
import type { TokenSource, UserToken } from './sso/index.js';

export const CALL_API_VERB = 'callApi';

// Universal Actions (Action.Execute) need card version 1.4+. The `action` data field is what the
// Teams SDK router uses for `card.action.<name>` routes.
const callApiAction = (title: string) => ({
  type: 'Action.Execute' as const,
  title,
  verb: CALL_API_VERB,
  data: { action: CALL_API_VERB },
});

const SOURCE_LABELS: Record<TokenSource, string> = {
  'sso+obo': 'Teams SSO, then the bot ran on-behalf-of with MSAL',
  'sso+token-service': 'Teams SSO, then the Bot Framework token service ran the OBO exchange',
  'token-service-cache': 'Cached in the Bot Framework token store',
  'sign-in-fallback': 'Interactive sign-in fallback (consent / MFA was needed)',
};

const TEAMS_CLIENTS: Record<string, string> = {
  '1fec8e78-bce4-4aaf-ab1b-5451cc387264': 'Teams desktop/mobile',
  '5e3ce6c0-2b1f-4285-8d4b-75ee78787346': 'Teams web',
};

export function welcomeCard(): IAdaptiveCard {
  return {
    type: 'AdaptiveCard',
    version: '1.5',
    body: [
      { type: 'TextBlock', text: 'Teams SSO + on-behalf-of demo', size: 'Large', weight: 'Bolder', wrap: true },
      {
        type: 'TextBlock',
        wrap: true,
        text:
          'Select the button. The bot asks Teams for a single sign-on token, exchanges it on your behalf for a ' +
          'token to the downstream API, and calls the API as you. If consent is already granted, you see no popup.',
      },
    ],
    actions: [callApiAction('Call downstream API')],
  };
}

export function resultCard(result: DownstreamResult, token: UserToken, showDiagnostics: boolean): IAdaptiveCard {
  const fact = (title: string, value: unknown) => ({ title, value: formatValue(value) });
  const claimsSections: IAdaptiveCard['body'] = [];

  if (showDiagnostics) {
    claimsSections.push(
      { type: 'TextBlock', text: 'Downstream token (as the API saw it)', weight: 'Bolder', wrap: true, spacing: 'Medium' },
      {
        type: 'FactSet',
        facts: [
          fact('aud', result.token.aud),
          fact('scp', result.token.scp),
          fact('azp/appid (caller)', result.token.callingAppId),
          fact('ver', result.token.ver),
          fact('expires', result.token.expiresAt),
        ],
      },
    );
    if (token.ssoClaims) {
      const azp = token.ssoClaims.azp ?? token.ssoClaims.appid;
      claimsSections.push(
        { type: 'TextBlock', text: 'Teams SSO token (sent to the bot)', weight: 'Bolder', wrap: true, spacing: 'Medium' },
        {
          type: 'FactSet',
          facts: [
            fact('aud', token.ssoClaims.aud),
            fact('scp', token.ssoClaims.scp),
            fact('azp/appid (caller)', azp && TEAMS_CLIENTS[azp] ? `${azp} (${TEAMS_CLIENTS[azp]})` : azp),
            fact('ver', token.ssoClaims.ver),
          ],
        },
      );
    }
  }

  return {
    type: 'AdaptiveCard',
    version: '1.5',
    body: [
      { type: 'TextBlock', text: 'Downstream API call succeeded', size: 'Large', weight: 'Bolder', color: 'Good', wrap: true },
      { type: 'TextBlock', text: result.message, wrap: true },
      {
        type: 'FactSet',
        facts: [
          fact('User', result.user.name),
          fact('Username', result.user.username),
          fact('Object ID', result.user.oid),
          fact('Token obtained via', SOURCE_LABELS[token.source]),
        ],
      },
      ...(claimsSections ?? []),
    ],
    actions: [callApiAction('Call again')],
  };
}

export function errorCard(title: string, detail: string): IAdaptiveCard {
  return {
    type: 'AdaptiveCard',
    version: '1.5',
    body: [
      { type: 'TextBlock', text: title, size: 'Large', weight: 'Bolder', color: 'Attention', wrap: true },
      { type: 'TextBlock', text: detail, wrap: true, fontType: 'Monospace', size: 'Small' },
    ],
    actions: [callApiAction('Try again')],
  };
}

function formatValue(value: unknown): string {
  if (value === undefined || value === null || value === '') return '-';
  return Array.isArray(value) ? value.join(', ') : String(value);
}
