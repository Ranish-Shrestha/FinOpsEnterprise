import { authConfig } from './auth.config';

/** The deployable settings in `/config.json`. Written by the deploy workflow; public identifiers only. */
export interface RuntimeConfig {
  apiBaseUrl: string;
  mockMode: boolean;
  tenantId: string;
  spaClientId: string;
  apiScope: string;
  /** Optional Google OAuth client ID for direct "Sign in with Google". */
  googleClientId?: string;
}

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const googleClient = /^[\w-]+\.apps\.googleusercontent\.com$/;

/**
 * Validates and applies a runtime configuration. Rejects anything that would send credentials somewhere unexpected:
 * the API base URL must be empty (same origin) or an https origin with no path, Entra needs real IDs, and a Google
 * client ID must look like one.
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
  const googleClientId = config.googleClientId ?? '';
  if (googleClientId !== '' && !googleClient.test(googleClientId)) {
    throw new Error('googleClientId must end with .apps.googleusercontent.com.');
  }
  // Without mock mode, Entra is required unless Google is configured and no Entra value is given.
  const entraRequested = !!(config.tenantId || config.spaClientId || config.apiScope) || googleClientId === '';
  if (!mockMode && entraRequested) {
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
  authConfig.entraEnabled = !mockMode && entraRequested;
  authConfig.googleClientId = mockMode ? '' : googleClientId;
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
