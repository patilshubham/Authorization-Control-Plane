import { useEffect, useMemo, useState } from "react";
import { useQueries } from "@tanstack/react-query";
import {
  useAllAssignments,
  useApplications,
  useCreateTenant,
  useDeleteTenant,
  useReviewAccess,
  useTenants,
  useUpdateTenant,
  useUsersDirectoryPaged,
} from "../api/hooks";
import { useAiFeature } from "../api/aiConfig";
import { portalApi } from "../apiClient";
import type { AccessReviewItem } from "../apiClient";
import { qk } from "../api/queryKeys";
import type {
  ApplicationSummary,
  AssignmentSummary,
  RoleSummary,
  TenantSummary,
  UserDirectoryEntry,
} from "../types";
import {
  EmptyBlock,
  Field,
  RiskDot,
  SlideOver,
  Spinner,
} from "../components/primitives";
import { AppIcon } from "../components/icons";
import {
  ConfirmDialog,
  DataTable,
  DrawerPanel,
  StatusChip,
  usePageSizeState,
  type DataTableColumn,
} from "../ui";
import { useCapabilities } from "../capabilities";
import { ApplicationForm } from "./CreateForms";
import { displayState, formatDate } from "./formatters";
import { VizModal } from "../components/viz/VizModal";
import { D3Tree } from "../components/viz/D3Tree";
import {
  buildUserFootprintTree,
  type FootprintTenant,
} from "../components/viz/graphModel";

// ── Tenants ──────────────────────────────────────────────────────────────────

export function TenantsPanel({
  onSelectApp,
  onSelectTenant,
}: {
  onSelectApp?: (appId: string) => void;
  onSelectTenant?: (tenantId: string) => void;
}) {
  const tenants = useTenants();
  const applications = useApplications();
  const createTenant = useCreateTenant();
  const deleteTenant = useDeleteTenant();
  const canManagePlatform = useCapabilities().canManagePlatform;

  const [open, setOpen] = useState(false);
  const [tenantId, setTenantId] = useState("");
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [editTarget, setEditTarget] = useState<TenantSummary | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<TenantSummary | null>(null);
  const [createAppTenant, setCreateAppTenant] = useState<string | null>(null);
  const [status, setStatus] = useState("");

  const appsByTenant = useMemo(() => {
    const map = new Map<string, ApplicationSummary[]>();
    for (const app of applications.data ?? []) {
      const key = app.tenantId ?? "__none__";
      if (!map.has(key)) map.set(key, []);
      map.get(key)!.push(app);
    }
    return map;
  }, [applications.data]);

  const canSubmit =
    !!tenantId.trim() && !!name.trim() && !createTenant.isPending;

  const submit = () =>
    createTenant.mutate(
      {
        tenantId: tenantId.trim(),
        name: name.trim(),
        description: description.trim() || undefined,
      },
      {
        onSuccess: () => {
          setOpen(false);
          setTenantId("");
          setName("");
          setDescription("");
        },
      },
    );

  const columns: DataTableColumn<TenantSummary>[] = [
    {
      key: "name",
      header: "Tenant",
      sortValue: (t) => t.name,
      searchValue: (t) => `${t.name} ${t.tenantId}`,
      render: (t) =>
        onSelectTenant ? (
          <button
            type="button"
            className="link-cell cell-stack"
            onClick={() => onSelectTenant(t.tenantId)}
          >
            {t.name}
            <span className="cell-sub">{t.tenantId}</span>
          </button>
        ) : (
          <span className="cell-stack">
            {t.name}
            <span className="cell-sub">{t.tenantId}</span>
          </span>
        ),
    },
    {
      key: "apps",
      header: "Applications",
      sortValue: (t) => appsByTenant.get(t.tenantId)?.length ?? 0,
      render: (t) => {
        const apps = appsByTenant.get(t.tenantId) ?? [];
        if (apps.length === 0) return <span className="muted">None yet</span>;
        return (
          <span className="chip-row">
            {apps.map((a) => (
              <button
                key={a.applicationId}
                type="button"
                className="link-chip"
                onClick={() => onSelectApp?.(a.applicationId)}
              >
                {a.name}
              </button>
            ))}
          </span>
        );
      },
    },
    {
      key: "status",
      header: "Status",
      sortValue: (t) => t.status,
      render: (t) => <StatusChip value={t.status} />,
    },
    {
      key: "description",
      header: "Description",
      searchValue: (t) => t.description ?? "",
      render: (t) => <span className="muted">{t.description || "—"}</span>,
    },
  ];

  const rows = (tenants.data ?? []).filter(
    (t) => !status || t.status === status,
  );

  return (
    <>
      <header className="page-head">
        <div className="page-head-titles">
          <h1>Tenants</h1>
          <p className="page-sub">
            Organizations that own applications — the top of the hierarchy.
          </p>
        </div>
        <div className="page-head-actions">
          {canManagePlatform && (
            <button
              type="button"
              className="btn-secondary"
              onClick={() => setCreateAppTenant("")}
            >
              New application
            </button>
          )}
          {canManagePlatform && (
            <button
              type="button"
              className="btn-primary"
              onClick={() => setOpen(true)}
            >
              New tenant
            </button>
          )}
        </div>
      </header>

      <DataTable
        columns={columns}
        rows={rows}
        getRowKey={(t) => t.tenantId}
        isLoading={tenants.isLoading || applications.isLoading}
        searchPlaceholder="Search tenants…"
        filters={[
          {
            key: "status",
            label: "Status",
            value: status,
            onChange: setStatus,
            options: [
              { value: "", label: "All statuses" },
              ...Array.from(new Set((tenants.data ?? []).map((t) => t.status)))
                .sort()
                .map((s) => ({ value: s, label: s })),
            ],
          },
        ]}
        onClearFilters={() => setStatus("")}
        emptyMessage="No tenants yet. Create a tenant to own applications."
        emptyAction={
          canManagePlatform
            ? { label: "New tenant", onClick: () => setOpen(true) }
            : undefined
        }
        rowActions={
          canManagePlatform
            ? (t) => (
                <span className="chip-row">
                  <button
                    type="button"
                    className="mini-btn"
                    onClick={() => setCreateAppTenant(t.tenantId)}
                  >
                    New app
                  </button>
                  <button
                    type="button"
                    className="mini-btn"
                    onClick={() => setEditTarget(t)}
                  >
                    Edit
                  </button>
                  <button
                    type="button"
                    className="mini-btn danger"
                    onClick={() => setDeleteTarget(t)}
                  >
                    Delete
                  </button>
                </span>
              )
            : undefined
        }
      />

      <SlideOver
        open={open}
        title="New tenant"
        onClose={() => setOpen(false)}
        footer={
          <>
            <button
              type="button"
              className="btn-secondary"
              onClick={() => setOpen(false)}
            >
              Cancel
            </button>
            <button
              type="button"
              className="btn-primary"
              disabled={!canSubmit}
              onClick={submit}
            >
              {createTenant.isPending ? "Creating…" : "Create tenant"}
            </button>
          </>
        }
      >
        <Field
          label="Tenant ID"
          required
          hint="A short, stable slug (e.g. acme). Cannot be changed later."
        >
          <input
            value={tenantId}
            onChange={(e) => setTenantId(e.target.value)}
            placeholder="acme"
            aria-required="true"
          />
        </Field>
        <Field label="Name" required>
          <input
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Acme Corporation"
            aria-required="true"
          />
        </Field>
        <Field
          label="Description"
          hint="Optional — what this tenant represents."
        >
          <input
            value={description}
            onChange={(e) => setDescription(e.target.value)}
            placeholder="Primary enterprise tenant"
          />
        </Field>
      </SlideOver>

      {editTarget && (
        <TenantEditForm
          tenant={editTarget}
          onClose={() => setEditTarget(null)}
        />
      )}

      {createAppTenant !== null && (
        <ApplicationForm
          initialTenantId={createAppTenant}
          onClose={() => setCreateAppTenant(null)}
        />
      )}

      <ConfirmDialog
        open={!!deleteTarget}
        title="Delete tenant?"
        message={
          deleteTarget &&
          (appsByTenant.get(deleteTarget.tenantId)?.length ?? 0) > 0
            ? `“${deleteTarget.name}” still owns ${appsByTenant.get(deleteTarget.tenantId)!.length} application(s). Re-parent or remove them before deleting the tenant.`
            : `“${deleteTarget?.name}” will be permanently deleted. This cannot be undone.`
        }
        confirmLabel="Delete"
        danger
        confirmDisabled={
          !!deleteTarget &&
          (appsByTenant.get(deleteTarget.tenantId)?.length ?? 0) > 0
        }
        onCancel={() => setDeleteTarget(null)}
        onConfirm={() => {
          if (deleteTarget)
            deleteTenant.mutate(deleteTarget.tenantId, {
              onSuccess: () => setDeleteTarget(null),
            });
        }}
      />
    </>
  );
}

function TenantEditForm({
  tenant,
  onClose,
}: {
  tenant: TenantSummary;
  onClose: () => void;
}) {
  const update = useUpdateTenant();
  const [name, setName] = useState(tenant.name);
  const [description, setDescription] = useState(tenant.description ?? "");
  const [status, setStatus] = useState(tenant.status);

  const submit = () =>
    update.mutate(
      {
        tenantId: tenant.tenantId,
        name: name.trim(),
        description: description.trim() || undefined,
        status,
      },
      { onSuccess: onClose },
    );

  return (
    <SlideOver
      open
      title="Edit tenant"
      onClose={onClose}
      footer={
        <>
          <button type="button" className="btn-secondary" onClick={onClose}>
            Cancel
          </button>
          <button
            type="button"
            className="btn-primary"
            disabled={update.isPending || !name.trim()}
            onClick={submit}
          >
            {update.isPending ? "Saving…" : "Save changes"}
          </button>
        </>
      }
    >
      <Field
        label="Tenant ID"
        hint="Immutable — the stable identity of this tenant."
      >
        <input value={tenant.tenantId} readOnly disabled />
      </Field>
      <Field label="Name">
        <input value={name} onChange={(e) => setName(e.target.value)} />
      </Field>
      <Field label="Status">
        <select value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="ACTIVE">ACTIVE</option>
          <option value="DISABLED">DISABLED</option>
          <option value="ARCHIVED">ARCHIVED</option>
        </select>
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

// The Hierarchy explorer surface lives in its own module (Access Lens).
export { HierarchyExplorer } from "./AccessLens";

// ── Users ───────────────────────────────────────────────────────────────────

export function UsersPanel({
  onSelectUser,
}: {
  onSelectUser: (email: string) => void;
}) {
  const [q, setQ] = useState("");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = usePageSizeState();
  useEffect(() => setPage(1), [q, pageSize]);

  const users = useUsersDirectoryPaged({
    page,
    pageSize,
    q: q || undefined,
  });
  const rows = users.data?.items ?? [];

  const columns: DataTableColumn<UserDirectoryEntry>[] = [
    {
      key: "email",
      header: "User",
      sortValue: (u) => u.email,
      render: (u) => <span className="cell-strong">{u.email}</span>,
    },
    {
      key: "apps",
      header: "Applications",
      sortValue: (u) => u.applicationCount,
      render: (u) => <span>{u.applicationCount}</span>,
    },
    {
      key: "active",
      header: "Active",
      sortValue: (u) => u.activeCount,
      render: (u) => (
        <span className="expiry expiry-muted">{u.activeCount}</span>
      ),
    },
    {
      key: "expired",
      header: "Expired",
      sortValue: (u) => u.expiredCount,
      render: (u) =>
        u.expiredCount ? (
          <span className="expiry expiry-danger">{u.expiredCount}</span>
        ) : (
          <span className="muted">0</span>
        ),
    },
  ];

  return (
    <>
      <header className="page-head">
        <div className="page-head-titles">
          <h1>Users</h1>
          <p className="page-sub">
            Every subject holding at least one assignment across all
            applications.
          </p>
        </div>
      </header>

      <DataTable
        columns={columns}
        rows={rows}
        getRowKey={(u) => u.email}
        isLoading={users.isLoading}
        isError={users.isError}
        searchPlaceholder="Search users…"
        searchValue={q}
        onSearchChange={setQ}
        serverPagination={{
          page,
          pageSize,
          total: users.data?.total ?? 0,
          onPageChange: setPage,
          onPageSizeChange: setPageSize,
        }}
        emptyMessage="No users have assignments yet."
        rowActions={(u) => (
          <button
            type="button"
            className="mini-btn"
            onClick={() => onSelectUser(u.email)}
          >
            View access
          </button>
        )}
      />
    </>
  );
}

// ── User details (effective access hierarchy) ───────────────────────────────────

function AccessReviewRow({
  item,
  onOpenApp,
}: {
  item: AccessReviewItem;
  onOpenApp: () => void;
}) {
  const usage =
    item.lastUsedDaysAgo == null
      ? "Never exercised"
      : `Last used ${item.lastUsedDaysAgo} day${item.lastUsedDaysAgo === 1 ? "" : "s"} ago`;
  return (
    <li className="advisor-item">
      <span className="advisor-sev" title={`${item.riskLevel} risk`}>
        <RiskDot level={item.riskLevel} />
        <StatusChip value={item.recommendation} />
      </span>
      <span className="advisor-body">
        <span className="advisor-title">
          {item.roleName}
          {item.privileged && (
            <span className="muted"> · privileged</span>
          )}
          <button type="button" className="mini-btn" onClick={onOpenApp}>
            {item.applicationId}
          </button>
        </span>
        <span className="advisor-detail muted">
          {item.rationale ?? item.recommendationReason}
        </span>
        <span className="advisor-detail muted">
          {usage} · {item.peerCount} peer
          {item.peerCount === 1 ? "" : "s"} hold this role
        </span>
        {item.permissions.length > 0 && (
          <span className="sod-perm-chips">
            {item.permissions.map((perm) => (
              <span key={perm} className="sod-chip">
                {perm}
              </span>
            ))}
          </span>
        )}
      </span>
    </li>
  );
}

export function UserDetails({
  email,
  onSelectApp,
}: {
  email: string;
  onSelectApp: (appId: string) => void;
}) {
  const applications = useApplications();
  const apps = applications.data ?? [];
  const tenants = useTenants();
  const appIds = useMemo(() => apps.map((a) => a.applicationId), [apps]);
  const all = useAllAssignments(appIds);

  // Only fetch roles for applications where this user actually holds an assignment.
  const relevantAppIds = useMemo(() => {
    const ids = new Set<string>();
    for (const { appId, assignments } of all.byApp) {
      if (assignments.some((a) => a.subjectEmail === email)) ids.add(appId);
    }
    return [...ids];
  }, [all.byApp, email]);

  const roleQueries = useQueries({
    queries: relevantAppIds.map((id) => ({
      queryKey: qk.roles(id),
      queryFn: () => portalApi.listRoles(id),
      enabled: !!id,
      staleTime: 30_000,
    })),
  });
  const rolesByApp = useMemo(() => {
    const map = new Map<string, RoleSummary[]>();
    relevantAppIds.forEach((id, i) =>
      map.set(id, (roleQueries[i]?.data ?? []) as RoleSummary[]),
    );
    return map;
  }, [relevantAppIds, roleQueries]);

  const tenantName = (id: string | null | undefined) =>
    (id && tenants.data?.find((t) => t.tenantId === id)?.name) || "Unassigned";

  // Group this user's assignments by owning tenant → application.
  const grouped = useMemo(() => {
    const byTenant = new Map<
      string,
      {
        tenantName: string;
        apps: Map<
          string,
          { app: ApplicationSummary; assignments: AssignmentSummary[] }
        >;
      }
    >();
    for (const { appId, assignments } of all.byApp) {
      const mine = assignments.filter((a) => a.subjectEmail === email);
      if (mine.length === 0) continue;
      const app = apps.find((a) => a.applicationId === appId);
      if (!app) continue;
      const tKey = app.tenantId ?? "__none__";
      if (!byTenant.has(tKey))
        byTenant.set(tKey, {
          tenantName: tenantName(app.tenantId),
          apps: new Map(),
        });
      byTenant.get(tKey)!.apps.set(appId, { app, assignments: mine });
    }
    return byTenant;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [all.byApp, apps, email, tenants.data]);

  const totalActive = useMemo(() => {
    let n = 0;
    for (const { assignments } of all.byApp)
      n += assignments.filter(
        (a) => a.subjectEmail === email && displayState(a) === "ACTIVE",
      ).length;
    return n;
  }, [all.byApp, email]);

  const [footprintOpen, setFootprintOpen] = useState(false);
  const certificationEnabled = useAiFeature("accessCertification");
  const reviewAccess = useReviewAccess();
  const [reviewOpen, setReviewOpen] = useState(false);
  const runReview = () => {
    setReviewOpen(true);
    reviewAccess.mutate({ subjectEmail: email });
  };
  const review = reviewAccess.data;
  const flaggedCount = useMemo(
    () =>
      (review?.items ?? []).filter((i) => i.recommendation !== "KEEP").length,
    [review],
  );
  const footprintTree = useMemo(() => {
    const tenants: FootprintTenant[] = [...grouped.values()].map((tenant) => ({
      tenantName: tenant.tenantName,
      apps: [...tenant.apps.values()],
    }));
    return buildUserFootprintTree(email, tenants, displayState);
  }, [grouped, email]);

  if (applications.isLoading || all.isLoading)
    return <Spinner label="Loading user access…" />;

  return (
    <article className="inspector">
      <header className="inspector-head">
        <div className="inspector-title-row">
          <h1>{email}</h1>
          <span className="muted">
            {totalActive} active grant{totalActive === 1 ? "" : "s"}
          </span>
        </div>
        <p className="page-subtitle">
          Effective access explained top-down:{" "}
          <strong>Tenant → Application → Role (assignment) → Permissions</strong>
          . Each grant shows the causal chain — why the user has the access and
          when it expires.
        </p>
        <p className="why-chain-note">
          <AppIcon name="info" size={14} aria-hidden="true" />
          <span>
            This shows what the user’s roles <em>grant</em> (their effective
            capabilities). Whether a specific request is finally{" "}
            <strong>allowed or denied</strong> also depends on policy conditions
            evaluated at decision time — open an application’s{" "}
            <strong>Simulator</strong> to test a concrete request and see the
            conditions, rules and final decision.
          </span>
        </p>
        {grouped.size > 0 && (
          <div className="inspector-action-bar">
            <button
              type="button"
              className="btn-secondary btn-sm"
              onClick={() => setFootprintOpen(true)}
            >
              Access footprint
            </button>
            {certificationEnabled && (
              <button
                type="button"
                className="btn-secondary btn-sm"
                onClick={runReview}
                disabled={reviewAccess.isPending}
              >
                {reviewAccess.isPending ? "Reviewing…" : "Review access"}
              </button>
            )}
          </div>
        )}
      </header>

      {grouped.size === 0 ? (
        <EmptyBlock title="No access" hint="This user holds no assignments." />
      ) : (
        <div className="access-tree">
          {[...grouped.entries()].map(([tKey, tenant]) => (
            <section key={tKey} className="access-tenant">
              <h2 className="access-tenant-head">
                <span className="tree-ico" aria-hidden="true">
                  <AppIcon name="tenants" size={16} />
                </span>{" "}
                {tenant.tenantName}
              </h2>
              {[...tenant.apps.values()].map(({ app, assignments }) => (
                <div key={app.applicationId} className="access-app">
                  <div className="access-app-head">
                    <span className="tree-ico" aria-hidden="true">
                      <AppIcon name="applications" size={16} />
                    </span>
                    <strong>{app.name}</strong>
                    <button
                      type="button"
                      className="mini-btn"
                      onClick={() => onSelectApp(app.applicationId)}
                    >
                      Open
                    </button>
                  </div>
                  <ul className="access-grants">
                    {assignments.map((a, i) => {
                      const state = displayState(a);
                      const role = rolesByApp
                        .get(app.applicationId)
                        ?.find((r) => r.roleKey === a.roleKey);
                      return (
                        <li key={a.id ?? i} className="access-grant">
                          <div className="access-grant-head">
                            <span className="tree-ico" aria-hidden="true">
                              <AppIcon name="roles" size={16} />
                            </span>
                            <strong>{role?.name ?? a.roleKey}</strong>
                            {role?.privileged && (
                              <span
                                className="privileged-badge"
                                title="Privileged role"
                              >
                                ★ Privileged
                              </span>
                            )}
                            <StatusChip value={state} />
                            <span
                              className={`expiry expiry-${state === "EXPIRED" ? "danger" : "muted"}`}
                            >
                              {formatDate(a.validUntil, "No expiry")}
                            </span>
                          </div>
                          <p className="access-grant-why">
                            <span className="access-grant-why-label">Why:</span>{" "}
                            <code>{email}</code> is assigned role{" "}
                            <code>{a.roleKey}</code>
                            {a.reason ? ` (${a.reason})` : ""}, which{" "}
                            {state === "ACTIVE"
                              ? "grants"
                              : "would grant"}{" "}
                            the permissions below.
                            {state !== "ACTIVE" &&
                              " This assignment is not currently effective, so it grants nothing right now."}
                          </p>
                          {role && role.permissions.length > 0 && (
                            <div className="access-perms">
                              <span className="muted">
                                Permissions granted by this role (
                                {role.permissions.length}):
                              </span>
                              {role.permissions.map((p) => (
                                <code key={p} className="perm-chip">
                                  {p}
                                </code>
                              ))}
                            </div>
                          )}
                        </li>
                      );
                    })}
                  </ul>
                </div>
              ))}
            </section>
          ))}
        </div>
      )}

      <VizModal
        title={`${email} — access footprint`}
        subtitle="User → Tenant → Application → Role"
        open={footprintOpen}
        onClose={() => setFootprintOpen(false)}
        legend={
          <>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-tenant" /> Tenant
            </span>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-app" /> Application
            </span>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-role" /> Role
            </span>
          </>
        }
      >
        <D3Tree
          data={footprintTree}
          height={640}
          onSelect={(node) => {
            if (node.kind === "application") {
              onSelectApp(node.id.slice(node.id.indexOf(":") + 1));
              setFootprintOpen(false);
            }
          }}
          ariaLabel={`${email} access footprint`}
        />
      </VizModal>

      <DrawerPanel
        title="Access review"
        open={reviewOpen}
        onClose={() => setReviewOpen(false)}
      >
        {reviewAccess.isPending ? (
          <Spinner label="Reviewing access…" />
        ) : reviewAccess.isError ? (
          <EmptyBlock
            title="Review failed"
            hint="The access review could not be completed. Please try again."
          />
        ) : review && review.items.length > 0 ? (
          <div>
            <p className="muted" style={{ marginTop: 0 }}>
              Recommendations for <strong>{review.subjectEmail}</strong>
              {flaggedCount > 0
                ? ` — ${flaggedCount} of ${review.items.length} grant${review.items.length === 1 ? "" : "s"} flagged for attention.`
                : ` — nothing anomalous across ${review.items.length} grant${review.items.length === 1 ? "" : "s"}.`}
            </p>
            {review.summary && (
              <p className="advisor-summary">{review.summary}</p>
            )}
            <ul className="advisor-list">
              {review.items.map((item) => (
                <AccessReviewRow
                  key={item.id}
                  item={item}
                  onOpenApp={() => {
                    onSelectApp(item.applicationId);
                    setReviewOpen(false);
                  }}
                />
              ))}
            </ul>
          </div>
        ) : (
          <EmptyBlock
            title="No access to review"
            hint="This user holds no active grants in the applications you can view."
          />
        )}
      </DrawerPanel>
    </article>
  );
}
