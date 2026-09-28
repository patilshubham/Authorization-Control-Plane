// AccessGraph — the node-link counterpart to the Access Lens columns.
//
// It renders the same Tenant → Application → Role → Permission → Policy model as
// a horizontal tree. To stay light it mirrors the columns' lazy strategy: the
// full tenant/app breadth is always shown, but Role/Permission/Policy branches
// only expand along the current selection (the selected app's roles, the
// selected role's permissions, the selected permission's policies). Selecting a
// node drives the shared Lens selection so the breadcrumb stays in sync.

import { useMemo } from "react";
import { usePermissions, usePolicies, useRoles } from "../../api/hooks";
import type {
  ApplicationSummary,
  PermissionSummary,
  PolicySummary,
} from "../../types";
import { D3Tree } from "../../components/viz/D3Tree";
import type { GraphNodeDatum } from "../../components/viz/nodes";
import { Spinner } from "../../components/primitives";

export type GraphSelection = {
  tenantId?: string;
  appId?: string;
  roleKey?: string;
  permissionKey?: string;
};

type TenantRow = {
  tenantId: string;
  name: string;
  status: string;
  apps: ApplicationSummary[];
};

function matches(q: string, ...values: (string | null | undefined)[]): boolean {
  if (!q) return true;
  return values.some((v) => (v ?? "").toLowerCase().includes(q));
}

export function AccessGraph({
  tenantRows,
  selection,
  q,
  onSelectTenant,
  onSelectApp,
  onSelectRole,
  onSelectPermission,
}: {
  tenantRows: TenantRow[];
  selection: GraphSelection;
  q: string;
  onSelectTenant: (tenantId: string) => void;
  onSelectApp: (appId: string) => void;
  onSelectRole: (roleKey: string) => void;
  onSelectPermission: (permissionKey: string) => void;
}) {
  const appId = selection.appId ?? "";
  const roles = useRoles(appId);
  const permissions = usePermissions(appId);
  const policies = usePolicies(appId);

  const permByKey = useMemo(() => {
    const map = new Map<string, PermissionSummary>();
    for (const p of permissions.data ?? []) map.set(p.permissionKey, p);
    return map;
  }, [permissions.data]);

  const policiesByPermission = useMemo(() => {
    const map = new Map<string, PolicySummary[]>();
    for (const pol of policies.data ?? []) {
      if (!map.has(pol.permissionKey)) map.set(pol.permissionKey, []);
      map.get(pol.permissionKey)!.push(pol);
    }
    return map;
  }, [policies.data]);

  const tree = useMemo<GraphNodeDatum>(() => {
    return {
      id: "root:all",
      kind: "root",
      label: "All tenants",
      children: tenantRows.map((tenant) => {
        const tenantSelected = tenant.tenantId === selection.tenantId;
        const apps = tenant.apps.filter((a) =>
          matches(q, a.name, a.applicationId),
        );
        return {
          id: `tenant:${tenant.tenantId}`,
          kind: "tenant",
          label: tenant.name,
          sublabel: tenant.status === "—" ? undefined : tenant.status,
          badge: tenantSelected ? undefined : tenant.apps.length,
          children: tenantSelected
            ? apps.map((app) => buildAppNode(app))
            : undefined,
        } satisfies GraphNodeDatum;
      }),
    };

    function buildAppNode(app: ApplicationSummary): GraphNodeDatum {
      const appSelected = app.applicationId === selection.appId;
      const roleList = (roles.data ?? []).filter((r) =>
        matches(q, r.name, r.roleKey),
      );
      return {
        id: `application:${app.applicationId}`,
        kind: "application",
        label: app.name,
        sublabel: app.applicationId,
        riskLevel: app.riskLevel,
        children: appSelected
          ? roleList.map((role) => {
              const roleSelected = role.roleKey === selection.roleKey;
              return {
                id: `role:${role.roleKey}`,
                kind: "role",
                label: role.name,
                sublabel: role.roleKey,
                riskLevel: role.riskLevel,
                badge: roleSelected ? undefined : role.permissions.length,
                children: roleSelected
                  ? buildPermNodes(role.permissions)
                  : undefined,
              } satisfies GraphNodeDatum;
            })
          : undefined,
      };
    }

    function buildPermNodes(permissionKeys: string[]): GraphNodeDatum[] {
      return permissionKeys
        .map((key) => permByKey.get(key))
        .filter((p): p is PermissionSummary => !!p)
        .filter((p) => matches(q, p.permissionKey, p.resource, p.action))
        .map((perm) => {
          const permSelected = perm.permissionKey === selection.permissionKey;
          const govPolicies =
            policiesByPermission.get(perm.permissionKey) ?? [];
          return {
            id: `permission:${perm.permissionKey}`,
            kind: "permission",
            label: perm.permissionKey,
            sublabel: `${perm.resource} · ${perm.action}`,
            riskLevel: perm.riskLevel,
            badge: permSelected ? undefined : govPolicies.length,
            children: permSelected
              ? govPolicies.map((pol) => ({
                  id: `policy:${pol.policyKey}`,
                  kind: "policy",
                  label: pol.policyKey,
                  sublabel: `${pol.effect} · ${pol.state}`,
                  effect: pol.effect,
                }))
              : undefined,
          } satisfies GraphNodeDatum;
        });
    }
  }, [tenantRows, selection, q, roles.data, permByKey, policiesByPermission]);

  const selectedId =
    selection.permissionKey != null
      ? `permission:${selection.permissionKey}`
      : selection.roleKey != null
        ? `role:${selection.roleKey}`
        : selection.appId != null
          ? `application:${selection.appId}`
          : selection.tenantId != null
            ? `tenant:${selection.tenantId}`
            : undefined;

  const handleSelect = (node: GraphNodeDatum) => {
    const key = node.id.slice(node.id.indexOf(":") + 1);
    switch (node.kind) {
      case "tenant":
        onSelectTenant(key);
        break;
      case "application":
        onSelectApp(key);
        break;
      case "role":
        onSelectRole(key);
        break;
      case "permission":
        onSelectPermission(key);
        break;
      default:
        break;
    }
  };

  const deepLoading =
    !!selection.appId &&
    (roles.isLoading || permissions.isLoading || policies.isLoading);

  return (
    <div className="access-graph">
      {deepLoading && (
        <div className="access-graph-loading">
          <Spinner label="Loading branch…" />
        </div>
      )}
      <D3Tree
        data={tree}
        selectedId={selectedId}
        onSelect={handleSelect}
        ariaLabel="Access hierarchy graph"
      />
    </div>
  );
}
