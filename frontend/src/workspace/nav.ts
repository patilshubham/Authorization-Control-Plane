// Route path helpers — single source of truth for platform vs application URLs.
// The router is path-based so Platform vs Application scope is legible in the URL.
import type { Selection } from "./selection";

export const platformPaths = {
  root: "/platform",
  overview: "/platform",
  applications: "/platform/applications",
  tenants: "/platform/tenants",
  tenant: (tenantId: string) =>
    `/platform/tenants/${encodeURIComponent(tenantId)}`,
  users: "/platform/users",
  user: (email: string) => `/platform/users/${encodeURIComponent(email)}`,
  accessLens: "/platform/access-lens",
  audit: "/platform/audit",
  aiUsage: "/platform/ai-usage",
  askAi: "/platform/ask-ai",
  settings: "/platform/settings",
  profile: "/platform/profile",
};

export function appBase(appId: string): string {
  return `/app/${encodeURIComponent(appId)}`;
}

export const appPaths = {
  dashboard: (appId: string) => appBase(appId),
  roles: (appId: string) => `${appBase(appId)}/roles`,
  role: (appId: string, key: string) =>
    `${appBase(appId)}/roles/${encodeURIComponent(key)}`,
  permissions: (appId: string) => `${appBase(appId)}/permissions`,
  permission: (appId: string, key: string) =>
    `${appBase(appId)}/permissions/${encodeURIComponent(key)}`,
  policies: (appId: string) => `${appBase(appId)}/policies`,
  policy: (appId: string, key: string) =>
    `${appBase(appId)}/policies/${encodeURIComponent(key)}`,
  referenceData: (appId: string) => `${appBase(appId)}/reference-data`,
  matrix: (appId: string) => `${appBase(appId)}/matrix`,
  assignments: (appId: string) => `${appBase(appId)}/assignments`,
  identity: (appId: string) => `${appBase(appId)}/identity`,
  simulator: (appId: string) => `${appBase(appId)}/simulator`,
  decisions: (appId: string) => `${appBase(appId)}/decisions`,
  activity: (appId: string) => `${appBase(appId)}/activity`,
  certifications: (appId: string) => `${appBase(appId)}/certifications`,
  settings: (appId: string) => `${appBase(appId)}/settings`,
};

// Map a legacy Selection (still used inside app-scoped panels for entity focus)
// to its path under the current application. Platform-only kinds fall back to
// the platform area because they are out of scope for an application workspace.
export function selectionToAppPath(
  appId: string,
  selection: Selection,
): string {
  switch (selection.kind) {
    case "role":
      return selection.key
        ? appPaths.role(appId, selection.key)
        : appPaths.roles(appId);
    case "permission":
      return selection.key
        ? appPaths.permission(appId, selection.key)
        : appPaths.permissions(appId);
    case "policy":
      return selection.key
        ? appPaths.policy(appId, selection.key)
        : appPaths.policies(appId);
    case "application":
      return appPaths.settings(appId);
    case "matrix":
      return appPaths.matrix(appId);
    case "access":
      return appPaths.assignments(appId);
    case "identity":
      return appPaths.identity(appId);
    case "simulator":
      return appPaths.simulator(appId);
    case "activity":
      return appPaths.activity(appId);
    case "tenants":
      return platformPaths.tenants;
    case "users":
      return platformPaths.users;
    case "user":
      return platformPaths.user(selection.email);
    case "explorer":
      return platformPaths.accessLens;
    case "dashboard":
    default:
      return appPaths.dashboard(appId);
  }
}
