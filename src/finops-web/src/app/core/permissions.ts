/**
 * Permissions each page needs, shared by the route guards and the sidebar. They mirror what the API enforces (see
 * docs/architecture/ui-flow.md); the API remains the authority.
 */
export const pagePermissions = {
  budgets: ['budgets.read.department', 'budgets.read.all'],
  reports: ['reports.read.department', 'reports.read.all', 'audit.read'],
  outbox: ['messaging.manage'],
} as const;
