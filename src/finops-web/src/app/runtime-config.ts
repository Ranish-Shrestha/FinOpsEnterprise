import { authConfig } from './auth.config';

/** The deployable settings in `/config.json`. Written by the deploy workflow; public identifiers only. */
export interface RuntimeConfig {
  apiBaseUrl: string;
  mockMode: boolean;
  tenantId: string;
  spaClientId: string;
  apiScope: string;
}

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/**
 * Validates and applies a runtime configuration. Rejects anything that would send credentials somewhere unexpected:
 * the API base URL must be empty (same origin) or an https origin with no path, and Entra mode needs real IDs.
 */
export function applyRuntimeConfig(value: unknown): void {
  if (typeof value !== 'object' || value === null) throw new Error('config.json must be a JSON object.');
  const config = value as Partial<RuntimeConfig>;

  const apiBaseUrl = config.apiBaseUrl ?? '';
  if (apiBaseUrl !== '') {
    const url = new URL(apiBaseUrl);
    if (url.protocol !== 'https:' || url.origin !== apiBaseUrl) {
      throw new Error('apiBaseUrl must be an https origin without a path or trailing slash.');
    }
  }

  const mockMode = config.mockMode ?? authConfig.mockMode;
  if (!mockMode) {
    if (!guid.test(config.tenantId ?? '') || !guid.test(config.spaClientId ?? '')) {
      throw new Error('Entra mode needs tenantId and spaClientId GUIDs.');
    }
    if (!config.apiScope?.endsWith('/FinOps.Access')) throw new Error('apiScope must end with /FinOps.Access.');
  }

  authConfig.apiBaseUrl = apiBaseUrl;
  authConfig.mockMode = mockMode;
  if (config.tenantId) authConfig.tenantId = config.tenantId;
  if (config.spaClientId) authConfig.spaClientId = config.spaClientId;
  if (config.apiScope) authConfig.apiScope = config.apiScope;
}

/**
 * Loads `/config.json` before the app starts. A missing file keeps the local-development defaults; a present but
 * invalid file stops startup, because silently falling back to mock mode in a deployment would be wrong.
 */
export async function loadRuntimeConfig(fetchConfig: typeof fetch = fetch): Promise<void> {
  const response = await fetchConfig('/config.json', { cache: 'no-store' });
  if (response.status === 404) return;
  if (!response.ok) throw new Error(`config.json could not be loaded (${response.status}).`);
  applyRuntimeConfig(await response.json());
}
