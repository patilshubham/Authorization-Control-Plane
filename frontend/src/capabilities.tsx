import { createContext, useContext, useMemo, type ReactNode } from "react";

// Delegated-admin capability model. This MIRRORS the backend
// DelegatedAdminAuthorizationService so the UI hides controls the API would
// reject with 403. The backend remains the enforced security boundary; this is
// a least-privilege UX layer only.

export type Capability =
  | "ManageApplication"
  | "ManageRoles"
  | "ManagePermissions"
  | "MapRolePermission"
  | "ManagePolicies"
  | "AssignRoles"
  | "ViewAudit"
  | "ReadOnlyView";

const ROLES = {
  PlatformSuperAdmin: "PlatformSuperAdmin",
  PlatformReadOnlyViewer: "PlatformReadOnlyViewer",
  ApplicationAdmin: "ApplicationAdmin",
  ReadOnlyViewer: "ReadOnlyViewer",
  TenantAdmin: "TenantAdmin",
  TenantReadOnlyViewer: "TenantReadOnlyViewer",
} as const;

export const DELEGATED_ROLES = ROLES;

export const CAPABILITY_ROLES: Record<Capability, readonly string[]> = {
  ManageApplication: [ROLES.ApplicationAdmin],
  ManageRoles: [ROLES.ApplicationAdmin],
  ManagePermissions: [ROLES.ApplicationAdmin],
  MapRolePermission: [ROLES.ApplicationAdmin],
  ManagePolicies: [ROLES.ApplicationAdmin],
  AssignRoles: [ROLES.ApplicationAdmin],
  ViewAudit: [ROLES.ApplicationAdmin, ROLES.ReadOnlyViewer],
  ReadOnlyView: [ROLES.ApplicationAdmin, ROLES.ReadOnlyViewer],
};

// Tenant-scoped roles mirror their application-scoped counterparts across every
// application owned by the tenant. Keep in sync with the backend
// DelegatedAdminAuthorizationService.TenantCapabilityRoles.
export const TENANT_CAPABILITY_ROLES: Record<Capability, readonly string[]> = {
  ManageApplication: [ROLES.TenantAdmin],
  ManageRoles: [ROLES.TenantAdmin],
  ManagePermissions: [ROLES.TenantAdmin],
  MapRolePermission: [ROLES.TenantAdmin],
  ManagePolicies: [ROLES.TenantAdmin],
  AssignRoles: [ROLES.TenantAdmin],
  ViewAudit: [ROLES.TenantAdmin, ROLES.TenantReadOnlyViewer],
  ReadOnlyView: [ROLES.TenantAdmin, ROLES.TenantReadOnlyViewer],
};

export interface Capabilities {
  /** Create/update tenants and create applications. */
  readonly canManagePlatform: boolean;
  /** Whether the caller can see every application (platform roles / platform read-only viewer). */
  readonly canAccessAllApplications: boolean;
  /** Platform-scoped realm roles held by the signed-in caller. */
  readonly platformRoles: readonly string[];
  /** Per-application delegated roles held by the caller. */
  readonly appRoles: readonly {
    readonly applicationId: string;
    readonly role: string;
  }[];
  /** Tenant-scoped delegated roles held by the caller. */
  readonly tenantRoles: readonly {
    readonly tenantId: string;
    readonly role: string;
  }[];
  /** Check a per-application capability. */
  can(
    capability: Capability,
    applicationId: string | null | undefined,
  ): boolean;
}

function normalizeClaim(value: unknown): string[] {
  if (Array.isArray(value))
    return value.filter((item): item is string => typeof item === "string");
  if (typeof value === "string" && value.length > 0) return [value];
  return [];
}

function decodeAccessToken(token: string | null | undefined): {
  appRoles: string[];
  platformRoles: string[];
  tenantRoles: string[];
} {
  if (!token) return { appRoles: [], platformRoles: [], tenantRoles: [] };
  const segments = token.split(".");
  if (segments.length < 2)
    return { appRoles: [], platformRoles: [], tenantRoles: [] };
  try {
    const base64 = segments[1].replace(/-/g, "+").replace(/_/g, "/");
    const padded = base64.padEnd(
      base64.length + ((4 - (base64.length % 4)) % 4),
      "=",
    );
    const payload = JSON.parse(atob(padded)) as Record<string, unknown>;
    return {
      appRoles: normalizeClaim(payload["acp_app_role"]),
      platformRoles: normalizeClaim(payload["acp_platform_role"]),
      tenantRoles: normalizeClaim(payload["acp_tenant_role"]),
    };
  } catch {
    return { appRoles: [], platformRoles: [], tenantRoles: [] };
  }
}

export function parseCapabilities(
  accessToken: string | null | undefined,
  appTenants?: ReadonlyMap<string, string>,
): Capabilities {
  const { appRoles, platformRoles, tenantRoles } =
    decodeAccessToken(accessToken);
  const platform = new Set(platformRoles.map((role) => role.toLowerCase()));
  const isSuperAdmin = platform.has(ROLES.PlatformSuperAdmin.toLowerCase());
  const isPlatformReadOnlyViewer = platform.has(
    ROLES.PlatformReadOnlyViewer.toLowerCase(),
  );
  const fullAccess = isSuperAdmin;

  const appRoleSet = new Set(appRoles.map((role) => role.toLowerCase()));
  const hasAppRole = (applicationId: string, role: string) =>
    appRoleSet.has(`${applicationId}:${role}`.toLowerCase());

  const parsedAppRoles = appRoles
    .map((entry) => {
      const idx = entry.lastIndexOf(":");
      return idx > 0
        ? { applicationId: entry.slice(0, idx), role: entry.slice(idx + 1) }
        : null;
    })
    .filter(
      (entry): entry is { applicationId: string; role: string } =>
        entry !== null,
    );

  const tenantRoleSet = new Set(tenantRoles.map((role) => role.toLowerCase()));
  const hasTenantRole = (tenantId: string, role: string) =>
    tenantRoleSet.has(`${tenantId}:${role}`.toLowerCase());

  const parsedTenantRoles = tenantRoles
    .map((entry) => {
      const idx = entry.lastIndexOf(":");
      return idx > 0
        ? { tenantId: entry.slice(0, idx), role: entry.slice(idx + 1) }
        : null;
    })
    .filter(
      (entry): entry is { tenantId: string; role: string } => entry !== null,
    );

  // Resolves a per-application capability from any tenant role over the app's owning tenant.
  const hasTenantCapability = (
    capability: Capability,
    applicationId: string,
  ) => {
    const tenantId = appTenants?.get(applicationId);
    if (!tenantId) return false;
    return TENANT_CAPABILITY_ROLES[capability].some((role) =>
      hasTenantRole(tenantId, role),
    );
  };

  return {
    canManagePlatform: fullAccess,
    canAccessAllApplications: fullAccess || isPlatformReadOnlyViewer,
    platformRoles,
    appRoles: parsedAppRoles,
    tenantRoles: parsedTenantRoles,
    can(capability, applicationId) {
      if (!applicationId) return false;
      if (fullAccess) return true;
      if (isPlatformReadOnlyViewer) {
        return capability === "ViewAudit" || capability === "ReadOnlyView";
      }
      return (
        CAPABILITY_ROLES[capability].some((role) =>
          hasAppRole(applicationId, role),
        ) || hasTenantCapability(capability, applicationId)
      );
    },
  };
}

const CapabilitiesContext = createContext<Capabilities>(
  parseCapabilities(null),
);

export function CapabilitiesProvider({
  accessToken,
  appTenants,
  children,
}: {
  accessToken: string | null | undefined;
  /**
   * Application→owning-tenant map used to resolve tenant-scoped roles per application.
   * Supplied by the caller (which loads the backend-scoped applications list) because the
   * token alone does not carry an application's tenant. Omit it and tenant roles grant nothing.
   */
  appTenants?: ReadonlyMap<string, string>;
  children: ReactNode;
}) {
  const value = useMemo(
    () => parseCapabilities(accessToken, appTenants),
    [accessToken, appTenants],
  );
  return (
    <CapabilitiesContext.Provider value={value}>
      {children}
    </CapabilitiesContext.Provider>
  );
}

export function useCapabilities(): Capabilities {
  return useContext(CapabilitiesContext);
}
