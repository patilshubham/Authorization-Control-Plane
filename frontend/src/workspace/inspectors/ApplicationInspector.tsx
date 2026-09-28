import { useState } from "react";
import type { ApplicationSummary } from "../../types";
import {
  useApplicationLifecycle,
  useApplications,
  useTenants,
  useUpdateApplication,
} from "../../api/hooks";
import {
  Field,
  RiskDot,
  Segmented,
  SlideOver,
  Spinner,
} from "../../components/primitives";
import { ConfirmDialog, StatusChip } from "../../ui";
import {
  DEFAULT_POLICY_COMBINING_ALGORITHM,
  POLICY_COMBINING_ALGORITHMS,
  RISK_LEVELS,
} from "../../constants";
import { useCapabilities } from "../../capabilities";

// ── Application inspector ────────────────────────────────────────────────────────

// Map a stored combining-algorithm value to its human-readable label.
function combiningAlgorithmLabel(value: string | undefined): string {
  const match = POLICY_COMBINING_ALGORITHMS.find(
    (a) => a.value === (value ?? DEFAULT_POLICY_COMBINING_ALGORITHM),
  );
  return match?.label ?? DEFAULT_POLICY_COMBINING_ALGORITHM;
}

const LIFECYCLE_COPY: Record<string, { label: string; hint: string }> = {
  ACTIVE: {
    label: "Active",
    hint: "This application is live. Runtime authorization decisions are served for it.",
  },
  DISABLED: {
    label: "Disabled",
    hint: "Runtime authorization is suspended. Configuration is preserved and can be re-activated at any time.",
  },
  ARCHIVED: {
    label: "Archived",
    hint: "This application is retired and read-only. Restore it to resume configuration and runtime decisions.",
  },
};

export function ApplicationInspector({ appId }: { appId: string }) {
  const apps = useApplications();
  const tenants = useTenants();
  const { disable, archive, activate } = useApplicationLifecycle();
  const canManageApplication = useCapabilities().can(
    "ManageApplication",
    appId,
  );
  const [confirm, setConfirm] = useState<
    null | "disable" | "archive" | "activate"
  >(null);
  const [editing, setEditing] = useState(false);
  const app = apps.data?.find(
    (a: ApplicationSummary) => a.applicationId === appId,
  );
  if (!app) return <Spinner label="Loading application…" />;

  const status = app.status ?? "ACTIVE";
  const copy = LIFECYCLE_COPY[status] ?? LIFECYCLE_COPY.ACTIVE;
  const busy = disable.isPending || archive.isPending || activate.isPending;
  const owningTenant = app.tenantId
    ? tenants.data?.find((t) => t.tenantId === app.tenantId)
    : undefined;
  const owningTenantLabel = app.tenantId
    ? (owningTenant?.name ?? app.tenantId)
    : "Unassigned";

  const confirmContent = {
    disable: {
      title: "Disable application?",
      message: `Runtime authorization for “${app.name}” will be suspended immediately. Existing configuration is preserved and you can re-activate it later.`,
      confirmLabel: "Disable",
      danger: false,
      run: () => disable.mutate(appId),
    },
    archive: {
      title: "Archive application?",
      message: `“${app.name}” will be retired and become read-only. You can restore it later if needed.`,
      confirmLabel: "Archive",
      danger: true,
      run: () => archive.mutate(appId),
    },
    activate: {
      title: "Activate application?",
      message: `“${app.name}” will resume serving runtime authorization decisions.`,
      confirmLabel: "Activate",
      danger: false,
      run: () => activate.mutate(appId),
    },
  } as const;
  const active = confirm ? confirmContent[confirm] : null;

  return (
    <article className="inspector">
      <header className="inspector-head">
        <div className="inspector-title-row">
          <h1>{app.name}</h1>
          <StatusChip value={status} />
        </div>
        <p className="inspector-key">{app.applicationId}</p>
      </header>

      <section className="inspector-block">
        <div className="inspector-block-head">
          <h2>Details</h2>
          {canManageApplication && status !== "ARCHIVED" && (
            <button
              type="button"
              className="mini-btn"
              onClick={() => setEditing(true)}
            >
              Edit
            </button>
          )}
        </div>
        <dl className="prop-list">
          <div className="prop-row">
            <dt>Owning tenant</dt>
            <dd>{owningTenantLabel}</dd>
          </div>
          <div className="prop-row">
            <dt>Risk level</dt>
            <dd>
              <RiskDot level={app.riskLevel} /> <span>{app.riskLevel}</span>
            </dd>
          </div>
          <div className="prop-row">
            <dt>Policy combining</dt>
            <dd>{combiningAlgorithmLabel(app.policyCombiningAlgorithm)}</dd>
          </div>
          {app.ownerTeam && (
            <div className="prop-row">
              <dt>Owner team</dt>
              <dd>{app.ownerTeam}</dd>
            </div>
          )}
          {app.businessOwner && (
            <div className="prop-row">
              <dt>Business owner</dt>
              <dd>{app.businessOwner}</dd>
            </div>
          )}
          {app.technicalOwner && (
            <div className="prop-row">
              <dt>Technical owner</dt>
              <dd>{app.technicalOwner}</dd>
            </div>
          )}
          <div className="prop-row">
            <dt>Application ID</dt>
            <dd>
              <code className="prop-mono">{app.applicationId}</code>
            </dd>
          </div>
          {app.description && (
            <div className="prop-row prop-row-wide">
              <dt>Description</dt>
              <dd>{app.description}</dd>
            </div>
          )}
        </dl>
      </section>

      <section className="inspector-block">
        <h2>Lifecycle</h2>
        <div className="lifecycle-state">
          <StatusChip value={status} />
          <p className="muted">{copy.hint}</p>
        </div>
        <div className="inspector-action-bar">
          {canManageApplication && status !== "ACTIVE" && (
            <button
              type="button"
              className="btn-primary"
              disabled={busy}
              onClick={() => setConfirm("activate")}
            >
              {status === "ARCHIVED" ? "Restore" : "Activate"}
            </button>
          )}
          {canManageApplication && status === "ACTIVE" && (
            <button
              type="button"
              className="btn-secondary"
              disabled={busy}
              onClick={() => setConfirm("disable")}
            >
              Disable
            </button>
          )}
          {canManageApplication && status !== "ARCHIVED" && (
            <button
              type="button"
              className="btn-danger"
              disabled={busy}
              onClick={() => setConfirm("archive")}
            >
              Archive
            </button>
          )}
        </div>
      </section>

      {editing && (
        <ApplicationEditForm app={app} onClose={() => setEditing(false)} />
      )}

      <ConfirmDialog
        open={!!active}
        title={active?.title ?? ""}
        message={active?.message ?? ""}
        confirmLabel={active?.confirmLabel}
        danger={active?.danger}
        onCancel={() => setConfirm(null)}
        onConfirm={() => {
          active?.run();
          setConfirm(null);
        }}
      />
    </article>
  );
}

function ApplicationEditForm({
  app,
  onClose,
}: {
  app: ApplicationSummary;
  onClose: () => void;
}) {
  const update = useUpdateApplication();
  const tenants = useTenants();
  const [name, setName] = useState(app.name);
  const [description, setDescription] = useState(app.description ?? "");
  const [tenantId, setTenantId] = useState(app.tenantId ?? "");
  const [ownerTeam, setOwnerTeam] = useState(app.ownerTeam ?? "");
  const [businessOwner, setBusinessOwner] = useState(app.businessOwner ?? "");
  const [technicalOwner, setTechnicalOwner] = useState(
    app.technicalOwner ?? "",
  );
  const [riskLevel, setRiskLevel] = useState(app.riskLevel);
  const [combiningAlgorithm, setCombiningAlgorithm] = useState(
    app.policyCombiningAlgorithm ?? DEFAULT_POLICY_COMBINING_ALGORITHM,
  );

  const submit = () =>
    update.mutate(
      {
        applicationId: app.applicationId,
        name: name.trim(),
        description: description.trim() || undefined,
        tenantId,
        ownerTeam: ownerTeam.trim() || undefined,
        businessOwner: businessOwner.trim() || undefined,
        technicalOwner: technicalOwner.trim() || undefined,
        riskLevel,
        combiningAlgorithm,
      },
      { onSuccess: onClose },
    );

  return (
    <SlideOver
      open
      title="Edit application"
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn-secondary" onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            className="btn-primary"
            disabled={update.isPending || !name.trim() || !tenantId}
            onClick={submit}
          >
            {update.isPending ? "Saving…" : "Save changes"}
          </button>
        </>
      }
    >
      <Field
        label="Application ID"
        hint="Immutable — the stable identity of this application."
      >
        <input value={app.applicationId} readOnly disabled />
      </Field>
      <Field label="Name">
        <input value={name} onChange={(e) => setName(e.target.value)} />
      </Field>
      <Field
        label="Owning tenant"
        hint="Re-parent this application under a different tenant."
      >
        <select value={tenantId} onChange={(e) => setTenantId(e.target.value)}>
          <option value="" disabled>
            Select a tenant…
          </option>
          {(tenants.data ?? []).map((t) => (
            <option key={t.tenantId} value={t.tenantId}>
              {t.name}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Risk level">
        <Segmented
          ariaLabel="Risk level"
          value={riskLevel}
          onChange={setRiskLevel}
          options={RISK_LEVELS.map((r) => ({ value: r, label: r }))}
        />
      </Field>
      <Field
        label="Policy combining algorithm"
        hint="How overlapping policy decisions are reconciled at runtime."
      >
        <select
          value={combiningAlgorithm}
          onChange={(e) => setCombiningAlgorithm(e.target.value)}
        >
          {POLICY_COMBINING_ALGORITHMS.map((a) => (
            <option key={a.value} value={a.value}>
              {a.label}
            </option>
          ))}
        </select>
      </Field>
      <Field label="Owner team">
        <input
          value={ownerTeam}
          onChange={(e) => setOwnerTeam(e.target.value)}
        />
      </Field>
      <Field label="Business owner">
        <input
          value={businessOwner}
          onChange={(e) => setBusinessOwner(e.target.value)}
        />
      </Field>
      <Field label="Technical owner">
        <input
          value={technicalOwner}
          onChange={(e) => setTechnicalOwner(e.target.value)}
        />
      </Field>
      <Field label="Description">
        <input
          value={description}
          onChange={(e) => setDescription(e.target.value)}
        />
      </Field>
    </SlideOver>
  );
}
