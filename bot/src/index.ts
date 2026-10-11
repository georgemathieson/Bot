import { createBot } from './app.js';
import { loadConfig } from './config.js';

const config = loadConfig();
const app = createBot(config);

await app.start(config.port);
console.log(
  `bot listening on http://localhost:${config.port}/api/messages ` +
    `(strategy: ${config.tokenStrategy}, OAuth connection: ${config.oauthConnectionName}, SSO resource: ${config.botAppIdUri})`,
);
