import { usePermissions, usePolicies, useRoles } from "../api/hooks";
import { EmptyBlock, Spinner } from "../components/primitives";
import type { Selection } from "./selection";
import { RoleHub } from "./inspectors/RoleInspector";
import { PermissionInspector } from "./inspectors/PermissionInspector";
import { PolicyInspector } from "./inspectors/PolicyInspector";
import { ApplicationInspector } from "./inspectors/ApplicationInspector";

type InspectorProps = {
  appId: string;
  selection: Extract<
    Selection,
    { kind: "role" | "permission" | "policy" | "application" }
  >;
  onSelect: (s: Selection) => void;
};

export function Inspector({ appId, selection, onSelect }: InspectorProps) {
  const roles = useRoles(appId);
  const permissions = usePermissions(appId);
  const policies = usePolicies(appId);

  if (roles.isLoading || permissions.isLoading || policies.isLoading) {
    return <Spinner label="Loading workspace…" />;
  }

  if (selection.kind === "role") {
    const role = roles.data?.find((r) => r.roleKey === selection.key);
    if (!role)
      return (
        <EmptyBlock title="Role not found" hint="It may have been removed." />
      );
    return (
      <RoleHub
        appId={appId}
        role={role}
        permissions={permissions.data ?? []}
        policies={policies.data ?? []}
        onSelect={onSelect}
      />
    );
  }

  if (selection.kind === "permission") {
    const perm = permissions.data?.find(
      (p) => p.permissionKey === selection.key,
    );
    if (!perm) return <EmptyBlock title="Permission not found" />;
    return (
      <PermissionInspector
        appId={appId}
        perm={perm}
        roles={roles.data ?? []}
        policies={policies.data ?? []}
        onSelect={onSelect}
      />
    );
  }

  if (selection.kind === "policy") {
    const policy = policies.data?.find((p) => p.policyKey === selection.key);
    if (!policy) return <EmptyBlock title="Policy not found" />;
    return (
      <PolicyInspector
        appId={appId}
        policy={policy}
        roles={roles.data ?? []}
        onSelect={onSelect}
      />
    );
  }

  return <ApplicationInspector appId={appId} />;
}
