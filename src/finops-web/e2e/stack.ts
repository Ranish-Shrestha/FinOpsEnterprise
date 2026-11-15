import { join } from 'node:path';

/** Ports used only by the E2E stack, so it can run next to a development stack (4200/7143). */
export const E2E_API_URL = 'http://127.0.0.1:5199';
export const E2E_UI_URL = 'http://127.0.0.1:4300';

const configuration = process.env['E2E_BUILD_CONFIGURATION'] ?? 'Release';
const src = join(__dirname, '..', '..');
export const apiDirectory = join(src, 'FinOps.Api', 'bin', configuration, 'net10.0');
export const workerDirectory = join(src, 'FinOps.Worker', 'bin', configuration, 'net10.0');

function connectionString(): string {
  const value = process.env['E2E_CONNECTION_STRING'];
  if (!value) {
    throw new Error('Set E2E_CONNECTION_STRING to a migrated, seeded database (see README "End-to-end tests").');
  }
  return value;
}

/**
 * Explicit settings win over user secrets, so a developer's Entra or connection settings can't leak into the run.
 * Mock identities are allowed only because ASPNETCORE_ENVIRONMENT is Development (enforced by the API at startup).
 */
export function apiEnvironment(): Record<string, string> {
  return {
    ASPNETCORE_ENVIRONMENT: 'Development',
    ConnectionStrings__FinOps: connectionString(),
    Security__UseDevelopmentMockAuthentication: 'true',
    RateLimiting__PermitLimit: '10000',
  };
}

export function workerEnvironment(): Record<string, string> {
  return {
    DOTNET_ENVIRONMENT: 'Development',
    ConnectionStrings__FinOps: connectionString(),
    Messaging__PollIntervalSeconds: '1',
  };
}
