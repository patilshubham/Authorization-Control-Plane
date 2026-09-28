// Pure tree-model builders for the D3 hierarchy views.
//
// These turn already-loaded governance data into the `GraphNodeDatum` shape the
// D3Tree renders. Keeping them here (a) avoids duplicating the wiring across the
// Dashboard, inspectors, user footprint and platform maps, and (b) keeps them
// framework-free so they unit-test without a DOM.

import type {
  ApplicationSummary,
  AssignmentSummary,
  PermissionSummary,
  PolicySummary,
  RoleSummary,
} from "../../types";
import type { GraphNodeDatum } from "./nodes";

function indexPermissions(
  permissions: PermissionSummary[],
): Map<string, PermissionSummary> {
  const map = new Map<string, PermissionSummary>();
  for (const p of permissions) map.set(p.permissionKey, p);
  return map;
}

function indexPoliciesByPermission(
  policies: PolicySummary[],
): Map<string, PolicySummary[]> {
  const map = new Map<string, PolicySummary[]>();
  for (const pol of policies) {
    const bucket = map.get(pol.permissionKey);
    if (bucket) bucket.push(pol);
    else map.set(pol.permissionKey, [pol]);
  }
  return map;
}

function policyNode(pol: PolicySummary): GraphNodeDatum {
  return {
    id: `policy:${pol.policyKey}`,
    kind: "policy",
    label: pol.policyKey,
    sublabel: `${pol.effect} · ${pol.state}`,
    effect: pol.effect,
  };
}

function permissionNode(
  perm: PermissionSummary,
  policiesByPermission: Map<string, PolicySummary[]>,
): GraphNodeDatum {
  const govPolicies = policiesByPermission.get(perm.permissionKey) ?? [];
  return {
    id: `permission:${perm.permissionKey}`,
    kind: "permission",
    label: perm.permissionKey,
    sublabel: `${perm.resource} · ${perm.action}`,
    riskLevel: perm.riskLevel,
    children: govPolicies.length > 0 ? govPolicies.map(policyNode) : undefined,
  };
}

function roleNode(
  role: RoleSummary,
  permByKey: Map<string, PermissionSummary>,
  policiesByPermission: Map<string, PolicySummary[]>,
): GraphNodeDatum {
  const perms = role.permissions
    .map((key) => permByKey.get(key))
    .filter((p): p is PermissionSummary => !!p)
    .map((perm) => permissionNode(perm, policiesByPermission));
  return {
    id: `role:${role.roleKey}`,
    kind: "role",
    label: role.name,
    sublabel: role.roleKey,
    riskLevel: role.riskLevel,
    children: perms.length > 0 ? perms : undefined,
  };
}

/** Application → Roles → Permissions → Policies (app-scoped, bounded). */
export function buildAppAccessTree(
  appId: string,
  appName: string,
  roles: RoleSummary[],
  permissions: PermissionSummary[],
  policies: PolicySummary[],
): GraphNodeDatum {
  const permByKey = indexPermissions(permissions);
  const policiesByPermission = indexPoliciesByPermission(policies);
  return {
    id: `application:${appId}`,
    kind: "application",
    label: appName,
    sublabel: appId,
    children: roles.map((r) => roleNode(r, permByKey, policiesByPermission)),
  };
}

/** Role → Permissions → Policies (single role, from the role inspector). */
export function buildRoleSubtree(
  role: RoleSummary,
  permissions: PermissionSummary[],
  policies: PolicySummary[],
): GraphNodeDatum {
  const permByKey = indexPermissions(permissions);
  const policiesByPermission = indexPoliciesByPermission(policies);
  return roleNode(role, permByKey, policiesByPermission);
}

/** Permission ← Roles that grant it, and → Policies that govern it (reverse view). */
export function buildPermissionReverseTree(
  perm: PermissionSummary,
  grantingRoles: RoleSummary[],
  relatedPolicies: PolicySummary[],
): GraphNodeDatum {
  const roleChildren = grantingRoles.map<GraphNodeDatum>((r) => ({
    id: `role:${r.roleKey}`,
    kind: "role",
    label: r.name,
    sublabel: r.roleKey,
    riskLevel: r.riskLevel,
  }));
  const policyChildren = relatedPolicies.map(policyNode);
  return {
    id: `permission:${perm.permissionKey}`,
    kind: "permission",
    label: perm.permissionKey,
    sublabel: `${perm.resource} · ${perm.action}`,
    riskLevel: perm.riskLevel,
    children: [...roleChildren, ...policyChildren],
  };
}

/** Policy → Permission → Roles it affects (impact view). */
export function buildPolicyImpactTree(
  policy: PolicySummary,
  perm: PermissionSummary | undefined,
  affectedRoles: RoleSummary[],
): GraphNodeDatum {
  const roleNodes = affectedRoles.map<GraphNodeDatum>((r) => ({
    id: `role:${r.roleKey}`,
    kind: "role",
    label: r.name,
    sublabel: r.roleKey,
    riskLevel: r.riskLevel,
  }));
  const permChild: GraphNodeDatum = {
    id: `permission:${policy.permissionKey}`,
    kind: "permission",
    label: policy.permissionKey,
    sublabel: perm ? `${perm.resource} · ${perm.action}` : undefined,
    riskLevel: perm?.riskLevel,
    children: roleNodes.length > 0 ? roleNodes : undefined,
  };
  return {
    id: `policy:${policy.policyKey}`,
    kind: "policy",
    label: policy.policyKey,
    sublabel: `${policy.effect} · ${policy.state}`,
    effect: policy.effect,
    children: [permChild],
  };
}

export type FootprintApp = {
  app: ApplicationSummary;
  assignments: AssignmentSummary[];
};
export type FootprintTenant = { tenantName: string; apps: FootprintApp[] };

/** User → Tenants → Applications → Roles (from the user's assignments). */
export function buildUserFootprintTree(
  email: string,
  tenants: FootprintTenant[],
  stateOf: (a: AssignmentSummary) => string,
): GraphNodeDatum {
  return {
    id: `root:${email}`,
    kind: "root",
    label: email,
    children: tenants.map((tenant, ti) => ({
      id: `tenant:${ti}:${tenant.tenantName}`,
      kind: "tenant",
      label: tenant.tenantName,
      children: tenant.apps.map(({ app, assignments }) => ({
        id: `application:${app.applicationId}`,
        kind: "application",
        label: app.name,
        sublabel: app.applicationId,
        riskLevel: app.riskLevel,
        children: assignments.map((a, ai) => ({
          id: `role:${app.applicationId}:${a.roleKey}:${ai}`,
          kind: "role",
          label: a.roleKey,
          sublabel: stateOf(a),
        })),
      })),
    })),
  };
}

export type TenantAppRow = {
  tenantId: string;
  name: string;
  status?: string;
  apps: AppLike[];
};

/** Minimal application shape shared by ApplicationSummary and TenantApplicationSummary. */
export type AppLike = {
  applicationId: string;
  name: string;
  riskLevel: string;
};

/** Root → Tenants → Applications (platform / portfolio map). */
export function buildTenantAppTree(
  rootLabel: string,
  tenantRows: TenantAppRow[],
): GraphNodeDatum {
  return {
    id: "root:all",
    kind: "root",
    label: rootLabel,
    children: tenantRows.map((tenant) => ({
      id: `tenant:${tenant.tenantId}`,
      kind: "tenant",
      label: tenant.name,
      sublabel:
        tenant.status && tenant.status !== "—" ? tenant.status : undefined,
      children: tenant.apps.map((app) => ({
        id: `application:${app.applicationId}`,
        kind: "application",
        label: app.name,
        sublabel: app.applicationId,
        riskLevel: app.riskLevel,
      })),
    })),
  };
}

/** Tenant → Applications (single tenant subtree). */
export function buildTenantSubtree(
  tenantId: string,
  tenantName: string,
  apps: AppLike[],
): GraphNodeDatum {
  return {
    id: `tenant:${tenantId}`,
    kind: "tenant",
    label: tenantName,
    children: apps.map((app) => ({
      id: `application:${app.applicationId}`,
      kind: "application",
      label: app.name,
      sublabel: app.applicationId,
      riskLevel: app.riskLevel,
    })),
  };
}

export type DecisionReason = {
  matchedRoles: string[];
  matchedPermissions: string[];
  matchedPolicies: string[];
};

/**
 * Subject → Roles → Permissions → Policies → Verdict as a single decision path.
 * Each layer lists what matched (in its sublabel) so the chain explains the
 * decision without fabricating cross-links the API does not provide.
 */
export function buildDecisionPath(
  subjectEmail: string,
  allowed: boolean,
  reason: DecisionReason | undefined,
  denyReason?: string | null,
): GraphNodeDatum {
  const summary = (items: string[]): string | undefined =>
    items.length === 0 ? "none" : items.join(", ");

  const verdict: GraphNodeDatum = {
    id: "verdict:decision",
    kind: "root",
    label: allowed ? "ALLOWED" : "DENIED",
    sublabel: allowed ? undefined : (denyReason ?? "no matching allow"),
    riskLevel: allowed ? "LOW" : "CRITICAL",
  };
  const policiesNode: GraphNodeDatum = {
    id: "layer:policies",
    kind: "policy",
    label: "Policies",
    sublabel: summary(reason?.matchedPolicies ?? []),
    badge: reason?.matchedPolicies.length,
    children: [verdict],
  };
  const permsNode: GraphNodeDatum = {
    id: "layer:permissions",
    kind: "permission",
    label: "Permissions",
    sublabel: summary(reason?.matchedPermissions ?? []),
    badge: reason?.matchedPermissions.length,
    children: [policiesNode],
  };
  const rolesNode: GraphNodeDatum = {
    id: "layer:roles",
    kind: "role",
    label: "Roles",
    sublabel: summary(reason?.matchedRoles ?? []),
    badge: reason?.matchedRoles.length,
    children: [permsNode],
  };
  return {
    id: `root:${subjectEmail || "subject"}`,
    kind: "root",
    label: subjectEmail || "subject",
    children: [rolesNode],
  };
}
