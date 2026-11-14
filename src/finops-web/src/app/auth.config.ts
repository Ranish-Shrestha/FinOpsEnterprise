export interface MockPersona { readonly objectId: string; readonly label: string; }

export const authConfig = {
  // Set these from the Entra app registrations before switching mockMode off.
  tenantId: 'YOUR_ENTRA_TENANT_ID',
  spaClientId: 'YOUR_SPA_CLIENT_ID',
  apiScope: 'api://YOUR_API_CLIENT_ID/FinOps.Access',
  redirectUri: `${window.location.origin}/`,
  mockMode: true,
  // Synthetic personas created by `dotnet run --project src/FinOps.Api -- seed-demo-data`.
  // The API trusts the mock header only in Development; these IDs mean nothing anywhere else.
  mockPersonas: [
    { objectId: '20000000-0000-0000-0000-000000000001', label: 'Demo Employee (Engineering)' },
    { objectId: '20000000-0000-0000-0000-000000000006', label: 'Demo Engineering Manager (approves Engineering)' },
    { objectId: '20000000-0000-0000-0000-000000000005', label: 'Demo Finance Officer (posts approved expenses)' },
    { objectId: '20000000-0000-0000-0000-000000000002', label: 'Demo Manager (Finance)' },
    { objectId: '20000000-0000-0000-0000-000000000003', label: 'Demo Administrator (reads all, audit)' },
    { objectId: '20000000-0000-0000-0000-000000000004', label: 'Demo Former Employee (inactive)' },
  ] satisfies readonly MockPersona[],
};
