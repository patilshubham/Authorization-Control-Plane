import { useState } from "react";
import type {
  PermissionSummary,
  PolicySummary,
  RoleSummary,
} from "../../types";
import {
  useDeletePermission,
  usePermissionLifecycle,
  useUpdatePermission,
} from "../../api/hooks";
import {
  Field,
  RiskDot,
  Segmented,
  SlideOver,
  StateBadge,
} from "../../components/primitives";
import { ConfirmDialog, StatusChip } from "../../ui";
import { RISK_LEVELS } from "../../constants";
import type { Selection } from "../selection";
import { useCapabilities } from "../../capabilities";
import { VizModal } from "../../components/viz/VizModal";
import { D3Tree } from "../../components/viz/D3Tree";
import { buildPermissionReverseTree } from "../../components/viz/graphModel";
import { nodeSelection } from "../../components/viz/nodeSelection";

// ── Permission inspector — reverse relationships ────────────────────────────────

export function PermissionInspector({
  appId,
  perm,
  roles,
  policies,
  onSelect,
}: {
  appId: string;
  perm: PermissionSummary;
  roles: RoleSummary[];
  policies: PolicySummary[];
  onSelect: (s: Selection) => void;
}) {
  const updatePermission = useUpdatePermission(appId);
  const deletePermission = useDeletePermission(appId);
  const permissionLifecycle = usePermissionLifecycle(appId);
  const canManagePermissions = useCapabilities().can(
    "ManagePermissions",
    appId,
  );
  const [editing, setEditing] = useState(false);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const [confirmLifecycle, setConfirmLifecycle] = useState<
    "disable" | "archive" | null
  >(null);
  const [graphOpen, setGraphOpen] = useState(false);
  const [form, setForm] = useState({
    description: perm.description ?? "",
    riskLevel: perm.riskLevel,
  });

  const openEdit = () => {
    setForm({ description: perm.description ?? "", riskLevel: perm.riskLevel });
    setEditing(true);
  };

  const savePermission = () =>
    updatePermission.mutate(
      {
        permissionKey: perm.permissionKey,
        description: form.description.trim() || undefined,
        riskLevel: form.riskLevel,
      },
      { onSuccess: () => setEditing(false) },
    );

  const removePermission = () =>
    deletePermission.mutate(perm.permissionKey, {
      onSuccess: () => {
        setConfirmDelete(false);
        onSelect({ kind: "dashboard" });
      },
    });

  const lifecyclePending =
    permissionLifecycle.disable.isPending ||
    permissionLifecycle.archive.isPending ||
    permissionLifecycle.activate.isPending;

  const applyLifecycle = () => {
    if (confirmLifecycle === "disable") {
      permissionLifecycle.disable.mutate(perm.permissionKey, {
        onSuccess: () => setConfirmLifecycle(null),
      });
    } else if (confirmLifecycle === "archive") {
      permissionLifecycle.archive.mutate(perm.permissionKey, {
        onSuccess: () => setConfirmLifecycle(null),
      });
    }
  };

  const grantingRoles = roles.filter((r) =>
    r.permissions.includes(perm.permissionKey),
  );
  const relatedPolicies = policies.filter(
    (p) => p.permissionKey === perm.permissionKey,
  );
  return (
    <article className="inspector">
      <header className="inspector-head">
        <div className="inspector-title-row">
          <RiskDot level={perm.riskLevel} />
          <h1>{perm.permissionKey}</h1>
          <StatusChip value={perm.status} />
        </div>
        <p className="inspector-key">
          {perm.resource} · {perm.action}
        </p>
        {perm.description && (
          <p className="inspector-desc">{perm.description}</p>
        )}
        <div className="inspector-action-bar">
          <button
            type="button"
            className="btn-secondary btn-sm"
            onClick={() => setGraphOpen(true)}
          >
            View graph
          </button>
          {canManagePermissions && (
            <button
              type="button"
              className="btn-secondary btn-sm"
              onClick={openEdit}
            >
              Edit permission
            </button>
          )}
          {canManagePermissions && perm.status !== "ACTIVE" && (
            <button
              type="button"
              className="btn-secondary btn-sm"
              disabled={lifecyclePending}
              onClick={() =>
                permissionLifecycle.activate.mutate(perm.permissionKey)
              }
            >
              {permissionLifecycle.activate.isPending
                ? "Activating…"
                : "Activate"}
            </button>
          )}
          {canManagePermissions && perm.status === "ACTIVE" && (
            <button
              type="button"
              className="btn-secondary btn-sm"
              disabled={lifecyclePending}
              onClick={() => setConfirmLifecycle("disable")}
            >
              Disable
            </button>
          )}
          {canManagePermissions && perm.status !== "ARCHIVED" && (
            <button
              type="button"
              className="btn-secondary btn-sm"
              disabled={lifecyclePending}
              onClick={() => setConfirmLifecycle("archive")}
            >
              Archive
            </button>
          )}
          {canManagePermissions && (
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
        title={`Edit ${perm.permissionKey}`}
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
              disabled={updatePermission.isPending}
              onClick={savePermission}
            >
              {updatePermission.isPending ? "Saving…" : "Save changes"}
            </button>
          </>
        }
      >
        <Field label="Permission key">
          <input value={perm.permissionKey} disabled readOnly />
        </Field>
        <Field
          label="Resource · Action"
          hint="Resource and action define this permission's identity and cannot be changed."
        >
          <input
            value={`${perm.resource} · ${perm.action}`}
            disabled
            readOnly
          />
        </Field>
        <Field
          label="Description"
          hint="Optional. Explains what this permission protects."
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
      </SlideOver>

      <ConfirmDialog
        open={confirmDelete}
        danger
        title={`Delete permission ${perm.permissionKey}?`}
        message="This permanently removes the permission. It will fail if any role still grants it or any policy references it."
        confirmLabel={
          deletePermission.isPending ? "Deleting…" : "Delete permission"
        }
        onConfirm={removePermission}
        onCancel={() => setConfirmDelete(false)}
      />

      <ConfirmDialog
        open={confirmLifecycle !== null}
        danger
        title={
          confirmLifecycle === "archive"
            ? `Archive permission ${perm.permissionKey}?`
            : `Disable permission ${perm.permissionKey}?`
        }
        message={
          confirmLifecycle === "archive"
            ? "Archiving retires this permission. It stops taking effect immediately and is hidden from active use, but its history is preserved. You can reactivate it later."
            : "Disabling stops this permission from taking effect immediately. Its role grants and policy references are kept but become inactive until it is reactivated."
        }
        confirmLabel={
          lifecyclePending
            ? "Working…"
            : confirmLifecycle === "archive"
              ? "Archive permission"
              : "Disable permission"
        }
        onConfirm={applyLifecycle}
        onCancel={() => setConfirmLifecycle(null)}
      />

      <section className="inspector-block">
        <div className="inspector-block-head">
          <h2>Granted to roles</h2>
          <span className="muted">{grantingRoles.length}</span>
        </div>
        {grantingRoles.length === 0 ? (
          <p className="muted">No role grants this permission yet.</p>
        ) : (
          <div className="chip-row">
            {grantingRoles.map((r) => (
              <button
                key={r.roleKey}
                type="button"
                className="chip chip-granted"
                onClick={() => onSelect({ kind: "role", key: r.roleKey })}
              >
                {r.name}
              </button>
            ))}
          </div>
        )}
      </section>

      <section className="inspector-block">
        <div className="inspector-block-head">
          <h2>Policies</h2>
          <span className="muted">{relatedPolicies.length}</span>
        </div>
        {relatedPolicies.length === 0 ? (
          <p className="muted">No policies reference this permission.</p>
        ) : (
          <ul className="relation-list">
            {relatedPolicies.map((p) => (
              <li key={p.policyKey}>
                <button
                  type="button"
                  className="relation-link"
                  onClick={() => onSelect({ kind: "policy", key: p.policyKey })}
                >
                  <span
                    className={`effect-pip effect-${p.effect.toLowerCase()}`}
                    aria-hidden="true"
                  />
                  {p.policyKey}
                </button>
                <span className="muted">{p.effect}</span>
                <StateBadge value={p.state} />
              </li>
            ))}
          </ul>
        )}
      </section>

      <VizModal
        title={`${perm.permissionKey} — reverse graph`}
        subtitle="Permission ← Roles · Policies"
        open={graphOpen}
        onClose={() => setGraphOpen(false)}
        legend={
          <>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-perm" /> Permission
            </span>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-role" /> Role
            </span>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-policy" /> Policy
            </span>
          </>
        }
      >
        <D3Tree
          data={buildPermissionReverseTree(
            perm,
            grantingRoles,
            relatedPolicies,
          )}
          height={640}
          onSelect={(node) => {
            const sel = nodeSelection(node);
            if (sel) {
              onSelect(sel);
              setGraphOpen(false);
            }
          }}
          ariaLabel={`${perm.permissionKey} reverse relationships`}
        />
      </VizModal>
    </article>
  );
}
