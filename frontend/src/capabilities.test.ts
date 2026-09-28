import { describe, expect, it } from "vitest";
import { parseCapabilities } from "./capabilities";

function makeToken(claims: Record<string, unknown>): string {
  const header = btoa(JSON.stringify({ alg: "none", typ: "JWT" }));
  const payload = btoa(JSON.stringify(claims))
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/, "");
  return `${header}.${payload}.signature`;
}

describe("parseCapabilities", () => {
  it("grants nothing when no token is provided", () => {
    const caps = parseCapabilities(null);
    expect(caps.canManagePlatform).toBe(false);
    expect(caps.canAccessAllApplications).toBe(false);
    expect(caps.can("ManageRoles", "finance-app")).toBe(false);
    expect(caps.can("ViewAudit", "finance-app")).toBe(false);
  });

  it("grants everything to a platform super admin", () => {
    const caps = parseCapabilities(
      makeToken({ acp_platform_role: ["PlatformSuperAdmin"] }),
    );
    expect(caps.canManagePlatform).toBe(true);
    expect(caps.canAccessAllApplications).toBe(true);
    expect(caps.can("ManageApplication", "finance-app")).toBe(true);
    expect(caps.can("ManageApplication", "any-other-app")).toBe(true);
  });

  it("limits a platform read-only viewer to read-only capabilities across all applications", () => {
    const caps = parseCapabilities(
      makeToken({ acp_platform_role: ["PlatformReadOnlyViewer"] }),
    );
    expect(caps.canManagePlatform).toBe(false);
    expect(caps.canAccessAllApplications).toBe(true);
    expect(caps.can("ViewAudit", "finance-app")).toBe(true);
    expect(caps.can("ReadOnlyView", "hr-app")).toBe(true);
    expect(caps.can("ManageRoles", "finance-app")).toBe(false);
  });

  it("scopes an application admin to its own application only", () => {
    const caps = parseCapabilities(
      makeToken({ acp_app_role: ["finance-app:ApplicationAdmin"] }),
    );
    expect(caps.canManagePlatform).toBe(false);
    expect(caps.canAccessAllApplications).toBe(false);
    expect(caps.can("ManageRoles", "finance-app")).toBe(true);
    expect(caps.can("ManageRoles", "hr-app")).toBe(false);
  });

  it("fails closed on a malformed token", () => {
    const caps = parseCapabilities("not-a-valid-jwt");
    expect(caps.canManagePlatform).toBe(false);
    expect(caps.canAccessAllApplications).toBe(false);
    expect(caps.can("ReadOnlyView", "finance-app")).toBe(false);
  });

  it("requires an application id for per-application checks", () => {
    const caps = parseCapabilities(
      makeToken({ acp_app_role: ["finance-app:ApplicationAdmin"] }),
    );
    expect(caps.can("ManageRoles", null)).toBe(false);
    expect(caps.can("ManageRoles", undefined)).toBe(false);
  });

  it("grants a tenant admin full capabilities across the tenant's applications", () => {
    const appTenants = new Map<string, string>([
      ["finance-app", "acme"],
      ["hr-app", "acme"],
      ["other-app", "globex"],
    ]);
    const caps = parseCapabilities(
      makeToken({ acp_tenant_role: ["acme:TenantAdmin"] }),
      appTenants,
    );
    expect(caps.canManagePlatform).toBe(false);
    expect(caps.canAccessAllApplications).toBe(false);
    expect(caps.tenantRoles).toEqual([{ tenantId: "acme", role: "TenantAdmin" }]);
    // Every application owned by the tenant is manageable.
    expect(caps.can("ManageRoles", "finance-app")).toBe(true);
    expect(caps.can("AssignRoles", "hr-app")).toBe(true);
    // Applications owned by a different tenant are not.
    expect(caps.can("ReadOnlyView", "other-app")).toBe(false);
    expect(caps.can("ManageRoles", "other-app")).toBe(false);
  });

  it("limits a tenant read-only viewer to read-only capabilities within the tenant", () => {
    const appTenants = new Map<string, string>([["finance-app", "acme"]]);
    const caps = parseCapabilities(
      makeToken({ acp_tenant_role: ["acme:TenantReadOnlyViewer"] }),
      appTenants,
    );
    expect(caps.can("ReadOnlyView", "finance-app")).toBe(true);
    expect(caps.can("ViewAudit", "finance-app")).toBe(true);
    expect(caps.can("ManageRoles", "finance-app")).toBe(false);
    expect(caps.can("AssignRoles", "finance-app")).toBe(false);
  });

  it("cannot resolve tenant capabilities without an application→tenant map", () => {
    // Without the map (e.g. applications not yet loaded) tenant roles grant nothing,
    // failing closed until the owning tenant of an application is known.
    const caps = parseCapabilities(
      makeToken({ acp_tenant_role: ["acme:TenantAdmin"] }),
    );
    expect(caps.can("ManageRoles", "finance-app")).toBe(false);
  });
});
