import { useMemo, useState } from "react";
import type {
  PermissionSummary,
  PolicySummary,
  RolePermissionSummary,
  RoleSummary,
} from "../../types";
import {
  useAssignments,
  useBulkGrantPermissions,
  useDeleteRole,
  useGrantPermission,
  useMappings,
  usePublishMapping,
  useRevokeAssignment,
  useRoleLifecycle,
  useUnmapPermission,
  useUpdateRole,
  useSodViolations,
} from "../../api/hooks";
import {
  EmptyBlock,
  Field,
  RiskDot,
  Segmented,
  SlideOver,
  StateBadge,
  Toggle,
} from "../../components/primitives";
import { ConfirmDialog, StatusChip } from "../../ui";
import { RISK_LEVELS } from "../../constants";
import type { Selection } from "../selection";
import { useCapabilities } from "../../capabilities";
import { useAiFeature } from "../../api/aiConfig";
import { VizModal } from "../../components/viz/VizModal";
import { D3Tree } from "../../components/viz/D3Tree";
import { buildRoleSubtree } from "../../components/viz/graphModel";
import { nodeSelection } from "../../components/viz/nodeSelection";

// ── Role hub — the flagship: metadata + grants + policies + holders in one place ─

export function RoleHub({
  appId,
  role,
  permissions,
  policies,
  onSelect,
}: {
  appId: string;
  role: RoleSummary;
  permissions: PermissionSummary[];
  policies: PolicySummary[];
  onSelect: (s: Selection) => void;
}) {
  const mappings = useMappings(appId);
  const assignments = useAssignments(appId);
  const grant = useGrantPermission(appId);
  const publish = usePublishMapping(appId);
  const unmap = useUnmapPermission(appId);
  const bulkGrant = useBulkGrantPermissions(appId);
  const revoke = useRevokeAssignment(appId);
  const updateRole = useUpdateRole(appId);
  const deleteRole = useDeleteRole(appId);
  const roleLifecycle = useRoleLifecycle(appId);
  const caps = useCapabilities();
  const canManageRoles = caps.can("ManageRoles", appId);
  const canMapRolePermission = caps.can("MapRolePermission", appId);
  const canAssignRoles = caps.can("AssignRoles", appId);

  const [editing, setEditing] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [confirmLifecycle, setConfirmLifecycle] = useState<
    "disable" | "archive" | null
  >(null);
  const [graphOpen, setGraphOpen] = useState(false);
  const [form, setForm] = useState({
    name: role.name,
    description: role.description ?? "",
    privileged: role.privileged,
    riskLevel: role.riskLevel,
  });

  const openEdit = () => {
    setForm({
      name: role.name,
      description: role.description ?? "",
      privileged: role.privileged,
      riskLevel: role.riskLevel,
    });
    setEditing(true);
  };

  const saveRole = () =>
    updateRole.mutate(
      {
        roleKey: role.roleKey,
        name: form.name.trim(),
        description: form.description.trim() || undefined,
        privileged: form.privileged,
        riskLevel: form.riskLevel,
      },
      { onSuccess: () => setEditing(false) },
    );

  const removeRole = () =>
    deleteRole.mutate(role.roleKey, {
      onSuccess: () => {
        setConfirmDelete(false);
        onSelect({ kind: "dashboard" });
      },
    });

  const lifecyclePending =
    roleLifecycle.disable.isPending ||
    roleLifecycle.archive.isPending ||
    roleLifecycle.activate.isPending;

  const applyLifecycle = () => {
    if (confirmLifecycle === "disable") {
      roleLifecycle.disable.mutate(role.roleKey, {
        onSuccess: () => setConfirmLifecycle(null),
      });
    } else if (confirmLifecycle === "archive") {
      roleLifecycle.archive.mutate(role.roleKey, {
        onSuccess: () => setConfirmLifecycle(null),
      });
    }
  };

  const mappingByPerm = useMemo(() => {
    const map = new Map<string, RolePermissionSummary>();
    for (const m of mappings.data ?? []) {
      if (m.roleKey === role.roleKey) map.set(m.permissionKey, m);
    }
    return map;
  }, [mappings.data, role.roleKey]);

  const grantedKeys = new Set(mappingByPerm.keys());
  const governingPolicies = policies.filter((p) =>
    grantedKeys.has(p.permissionKey),
  );
  const holders = (assignments.data ?? []).filter(
    (a) => a.roleKey === role.roleKey,
  );

  const sodEnabled = useAiFeature("sodAnalysis");
  const sodViolations = useSodViolations(appId, sodEnabled);
  const roleConflicts = (sodViolations.data?.violations ?? []).filter(
    (v) => v.scope === "ROLE" && v.deepLinkKey === role.roleKey,
  );

  const roleTree = useMemo(
    () => buildRoleSubtree(role, permissions, policies),
    [role, permissions, policies],
  );
  const hasGrants = role.permissions.length > 0;

  const toggleGrant = (perm: PermissionSummary, next: boolean) => {
    if (next) {
      grant.mutate({
        roleKey: role.roleKey,
        permissionKey: perm.permissionKey,
      });
    } else {
      const existing = mappingByPerm.get(perm.permissionKey);
      if (existing) unmap.mutate(existing.id);
    }
  };

  return (
    <article className="inspector">
      <header className="inspector-head">
        <div className="inspector-title-row">
          <RiskDot level={role.riskLevel} />
          <h1>{role.name}</h1>
          {role.privileged && (
            <span className="pill pill-privileged">Privileged</span>
          )}
          <StatusChip value={role.status} />
        </div>
        <p className="inspector-key">{role.roleKey}</p>
        {role.description && (
          <p className="inspector-desc">{role.description}</p>
        )}
        <div className="inspector-action-bar">
          {canManageRoles && (
            <button
              type="button"
              className="btn-secondary btn-sm"
              onClick={openEdit}
            >
              Edit role
            </button>
          )}
          {canManageRoles && role.status !== "ACTIVE" && (
            <button
              type="button"
              className="btn-secondary btn-sm"
              disabled={lifecyclePending}
              onClick={() => roleLifecycle.activate.mutate(role.roleKey)}
            >
              {roleLifecycle.activate.isPending ? "Activating…" : "Activate"}
            </button>
          )}
          {canManageRoles && role.status === "ACTIVE" && (
            <button
              type="button"
              className="btn-secondary btn-sm"
              disabled={lifecyclePending}
              onClick={() => setConfirmLifecycle("disable")}
            >
              Disable
            </button>
          )}
          {canManageRoles && role.status !== "ARCHIVED" && (
            <button
              type="button"
              className="btn-secondary btn-sm"
              disabled={lifecyclePending}
              onClick={() => setConfirmLifecycle("archive")}
            >
              Archive
            </button>
          )}
          {canManageRoles && (
            <button
              type="button"
              className="btn-danger btn-sm"
              onClick={() => setConfirmDelete(true)}
            >
              Delete
            </button>
          )}
        </div>
      </header>

      <SlideOver
        open={editing}
        title={`Edit ${role.roleKey}`}
        onClose={() => setEditing(false)}
        footer={
          <>
            <button
              type="button"
              className="btn-secondary"
              onClick={() => setEditing(false)}
            >
              Cancel
            </button>
            <button
              type="button"
              className="btn-primary"
              disabled={updateRole.isPending || !form.name.trim()}
              onClick={saveRole}
            >
              {updateRole.isPending ? "Saving…" : "Save changes"}
            </button>
          </>
        }
      >
        <Field label="Role key">
          <input value={role.roleKey} disabled readOnly />
        </Field>
        <Field label="Display name">
          <input
            value={form.name}
            onChange={(e) => setForm((f) => ({ ...f, name: e.target.value }))}
          />
        </Field>
        <Field
          label="Description"
          hint="Optional. Explains what this role is for."
        >
          <textarea
            rows={3}
            value={form.description}
            onChange={(e) =>
              setForm((f) => ({ ...f, description: e.target.value }))
            }
          />
        </Field>
        <Field label="Risk level">
          <Segmented
            ariaLabel="Risk level"
            value={form.riskLevel}
            onChange={(v) => setForm((f) => ({ ...f, riskLevel: v }))}
            options={RISK_LEVELS.map((r) => ({ value: r, label: r }))}
          />
        </Field>
        <Field
          label="Privileged"
          hint="Privileged roles grant elevated, sensitive access."
        >
          <Toggle
            checked={form.privileged}
            label="Privileged role"
            onChange={(v) => setForm((f) => ({ ...f, privileged: v }))}
          />
        </Field>
      </SlideOver>

      <ConfirmDialog
        open={confirmDelete}
        danger
        title={`Delete role ${role.roleKey}?`}
        message="This permanently removes the role. It will fail if the role still has active assignments or permission grants."
        confirmLabel={deleteRole.isPending ? "Deleting…" : "Delete role"}
        onConfirm={removeRole}
        onCancel={() => setConfirmDelete(false)}
      />

      <ConfirmDialog
        open={confirmLifecycle !== null}
        danger
        title={
          confirmLifecycle === "archive"
            ? `Archive role ${role.roleKey}?`
            : `Disable role ${role.roleKey}?`
        }
        message={
          confirmLifecycle === "archive"
            ? "Archiving retires this role. It stops granting access immediately and is hidden from active use, but assignment history is preserved. You can reactivate it later."
            : "Disabling stops this role from granting access immediately. Existing assignments are kept but become inactive until the role is reactivated."
        }
        confirmLabel={
          lifecyclePending
            ? "Working…"
            : confirmLifecycle === "archive"
              ? "Archive role"
              : "Disable role"
        }
        onConfirm={applyLifecycle}
        onCancel={() => setConfirmLifecycle(null)}
      />

      <section className="inspector-block">
        <div className="inspector-block-head">
          <h2>Permission grants</h2>
          <span className="muted">
            {grantedKeys.size} of {permissions.length} granted
          </span>
          {canMapRolePermission &&
            grantedKeys.size < permissions.length && (
              <button
                type="button"
                className="mini-btn"
                disabled={bulkGrant.isPending}
                onClick={() =>
                  bulkGrant.mutate(
                    permissions
                      .filter((p) => !grantedKeys.has(p.permissionKey))
                      .map((p) => ({
                        roleKey: role.roleKey,
                        permissionKey: p.permissionKey,
                      })),
                  )
                }
              >
                {bulkGrant.isPending
                  ? "Granting…"
                  : `Grant all remaining (${permissions.length - grantedKeys.size})`}
              </button>
            )}
        </div>
        <p className="inspector-hint">
          Toggle a permission to grant or revoke it for this role. Newly granted
          permissions start as a draft — publish to activate.
        </p>
        <ul className="grant-list">
          {permissions.map((perm) => {
            const mapping = mappingByPerm.get(perm.permissionKey);
            const granted = !!mapping;
            const busy = grant.isPending || publish.isPending;
            return (
              <li key={perm.permissionKey} className="grant-row">
                <Toggle
                  checked={granted}
                  disabled={busy || unmap.isPending || !canMapRolePermission}
                  label={`${granted ? "Revoke" : "Grant"} ${perm.permissionKey}`}
                  onChange={(next) => toggleGrant(perm, next)}
                />
                <button
                  type="button"
                  className="grant-key"
                  onClick={() =>
                    onSelect({ kind: "permission", key: perm.permissionKey })
                  }
                >
                  <RiskDot level={perm.riskLevel} />
                  {perm.permissionKey}
                </button>
                {mapping && (
                  <span className="grant-state">
                    <StateBadge value={mapping.state} />
                    {mapping.state !== "PUBLISHED" && canMapRolePermission && (
                      <button
                        type="button"
                        className="mini-btn"
                        disabled={publish.isPending}
                        onClick={() => publish.mutate(mapping.id)}
                      >
                        Publish
                      </button>
                    )}
                  </span>
                )}
              </li>
            );
          })}
          {permissions.length === 0 && (
            <EmptyBlock
              title="No permissions defined"
              hint="Create a permission to start granting access."
            />
          )}
        </ul>
      </section>

      {roleConflicts.length > 0 && (
        <section className="inspector-block">
          <div className="inspector-block-head">
            <h2>Separation-of-duties conflicts</h2>
            <span className="muted">{roleConflicts.length}</span>
          </div>
          <ul className="advisor-list">
            {roleConflicts.map((v, i) => (
              <li key={`${v.ruleKey}-${i}`} className="advisor-item">
                <span className="advisor-sev" title={`${v.severity} severity`}>
                  <RiskDot level={v.severity} />
                  <StatusChip value={v.severity} />
                </span>
                <span className="advisor-body">
                  <span className="advisor-title">{v.ruleName}</span>
                  <span className="advisor-detail muted">{v.detail}</span>
                  {v.conflictingPermissions.length > 0 && (
                    <span className="sod-perm-chips">
                      {v.conflictingPermissions.map((perm) => (
                        <span key={perm} className="sod-chip">
                          {perm}
                        </span>
                      ))}
                    </span>
                  )}
                </span>
              </li>
            ))}
          </ul>
        </section>
      )}

      <section className="inspector-block">
        <div className="inspector-block-head">
          <h2>Governing policies</h2>
          <span className="muted">{governingPolicies.length}</span>
        </div>
        {governingPolicies.length === 0 ? (
          <p className="muted">
            No policies constrain this role's granted permissions.
          </p>
        ) : (
          <ul className="relation-list">
            {governingPolicies.map((policy) => (
              <li key={policy.policyKey}>
                <button
                  type="button"
                  className="relation-link"
                  onClick={() =>
                    onSelect({ kind: "policy", key: policy.policyKey })
                  }
                >
                  <span
                    className={`effect-pip effect-${policy.effect.toLowerCase()}`}
                    aria-hidden="true"
                  />
                  {policy.policyKey}
                </button>
                <span className="muted">
                  {policy.effect} · {policy.permissionKey}
                </span>
                <StateBadge value={policy.state} />
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="inspector-block">
        <div className="inspector-block-head">
          <h2>Access subtree</h2>
          <button
            type="button"
            className="mini-btn ghost"
            disabled={!hasGrants}
            onClick={() => setGraphOpen(true)}
          >
            Expand
          </button>
        </div>
        {!hasGrants ? (
          <p className="muted">
            Grant a permission to see this role's subtree.
          </p>
        ) : (
          <div className="inspector-graph">
            <D3Tree
              data={roleTree}
              height={260}
              onSelect={(node) => {
                const sel = nodeSelection(node);
                if (sel) onSelect(sel);
              }}
              ariaLabel={`${role.name} access subtree`}
            />
          </div>
        )}
      </section>

      <section className="inspector-block">
        <div className="inspector-block-head">
          <h2>Who holds this role</h2>
          <span className="muted">{holders.length}</span>
        </div>
        {holders.length === 0 ? (
          <p className="muted">No active assignments.</p>
        ) : (
          <ul className="relation-list">
            {holders.map((a) => (
              <li key={`${a.subjectEmail}-${a.id ?? ""}`}>
                <span className="relation-link">{a.subjectEmail}</span>
                <StateBadge value={a.state} />
                {a.id && canAssignRoles && (
                  <button
                    type="button"
                    className="mini-btn danger"
                    disabled={revoke.isPending}
                    onClick={() => revoke.mutate(a.id!)}
                  >
                    Revoke
                  </button>
                )}
              </li>
            ))}
          </ul>
        )}
      </section>

      <VizModal
        title={`${role.name} — access subtree`}
        subtitle="Role → Permission → Policy"
        open={graphOpen}
        onClose={() => setGraphOpen(false)}
        legend={
          <>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-role" /> Role
            </span>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-perm" /> Permission
            </span>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-policy" /> Policy
            </span>
          </>
        }
      >
        <D3Tree
          data={roleTree}
          height={640}
          onSelect={(node) => {
            const sel = nodeSelection(node);
            if (sel) {
              onSelect(sel);
              setGraphOpen(false);
            }
          }}
          ariaLabel={`${role.name} access subtree`}
        />
      </VizModal>
    </article>
  );
}
