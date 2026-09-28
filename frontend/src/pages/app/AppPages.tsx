import { useEffect, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import {
  usePermissions,
  usePermissionsPaged,
  usePolicies,
  usePoliciesPaged,
  useReferenceDataPaged,
  useDeleteReferenceData,
  useRoles,
  useRolesPaged,
} from "../../api/hooks";
import { useUrlState } from "../../api/useUrlState";
import { Dashboard } from "../../workspace/Dashboard";
import { AccessMatrix } from "../../workspace/AccessMatrix";
import { DecisionAnalytics } from "../../workspace/DecisionAnalytics";
import { Certifications } from "../../workspace/Certifications";
import {
  AccessPanel,
  ActivityPanel,
  IdentityPanel,
  SimulatorPanel,
} from "../../workspace/panels";
import { Inspector } from "../../workspace/Inspector";
import { ReferenceDataForm } from "../../workspace/ReferenceDataForm";
import {
  EmptyBlock,
  RiskDot,
  Spinner,
  StateBadge,
} from "../../components/primitives";
import {
  ConfirmDialog,
  DataTable,
  StatusChip,
  usePageSizeState,
  type DataTableColumn,
} from "../../ui";
import { Breadcrumbs, type Crumb } from "../../scope/Scope";
import { useAppContext } from "../../shells/appContext";
import { useCapabilities } from "../../capabilities";
import {
  GOVERNANCE_STATUSES,
  POLICY_EFFECTS,
  POLICY_STATES,
} from "../../constants";
import {
  appPaths,
  platformPaths,
  selectionToAppPath,
} from "../../workspace/nav";
import type { Selection } from "../../workspace/selection";
import type {
  PermissionSummary,
  PolicySummary,
  ReferenceDataSummary,
  RoleSummary,
} from "../../types";

/** Title-cases an uppercase enum value for filter labels ("ACTIVE" → "Active"). */
const cap = (s: string) => (s ? s.charAt(0) + s.slice(1).toLowerCase() : s);

function useAppNav() {
  const { appId } = useAppContext();
  const navigate = useNavigate();
  return {
    appId,
    go: (s: Selection) => navigate(selectionToAppPath(appId, s)),
  };
}

// App-workspace breadcrumbs are always anchored to the scope trail
// (Platform → application → section) so an administrator can see where they
// are and always has a one-click path back to the global platform area.
function useAppCrumbs() {
  const { appId, app } = useAppContext();
  return (...tail: Crumb[]): Crumb[] => [
    { label: "Platform", to: platformPaths.applications },
    { label: app.name, to: appPaths.dashboard(appId) },
    ...tail,
  ];
}

export function AppDashboardPage() {
  const { appId, app, startCreate } = useAppContext();
  const { go } = useAppNav();
  return (
    <Dashboard
      appId={appId}
      appName={app.name}
      onSelect={go}
      onCreate={startCreate}
    />
  );
}

export function RolesPage() {
  const { appId, startCreate } = useAppContext();
  const canCreate = useCapabilities().can("ManageRoles", appId);
  const navigate = useNavigate();
  const crumbs = useAppCrumbs();
  const [status, setStatus] = useUrlState("status");
  const [privileged, setPrivileged] = useUrlState("privileged");
  const [q, setQ] = useUrlState("q");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = usePageSizeState();
  useEffect(() => setPage(1), [q, status, privileged, pageSize]);
  const roles = useRolesPaged(appId, {
    page,
    pageSize,
    q: q || undefined,
    status: status || undefined,
    privileged: privileged === "" ? undefined : privileged === "yes",
  });
  const columns: DataTableColumn<RoleSummary>[] = [
    {
      key: "name",
      header: "Role",
      sortValue: (r) => r.name,
      searchValue: (r) => `${r.name} ${r.roleKey}`,
      render: (r) => (
        <button
          type="button"
          className="link-cell"
          onClick={() => navigate(appPaths.role(appId, r.roleKey))}
        >
          <RiskDot level={r.riskLevel} /> {r.name}
        </button>
      ),
    },
    {
      key: "key",
      header: "Key",
      sortValue: (r) => r.roleKey,
      render: (r) => <code>{r.roleKey}</code>,
    },
    {
      key: "privileged",
      header: "Privileged",
      sortValue: (r) => (r.privileged ? "yes" : "no"),
      render: (r) => (r.privileged ? "★ Privileged" : "—"),
    },
    {
      key: "perms",
      header: "Permissions",
      sortValue: (r) => r.permissions.length,
      render: (r) => r.permissions.length,
    },
    {
      key: "status",
      header: "Status",
      sortValue: (r) => r.status,
      render: (r) => <StatusChip value={r.status} />,
    },
  ];
  const rows = roles.data?.items ?? [];
  return (
    <section className="page">
      <Breadcrumbs items={crumbs({ label: "Roles" })} />
      <header className="page-head">
        <div className="page-head-titles">
          <h1>Roles</h1>
          <p className="page-sub">
            Named bundles of permissions that are granted to users.
          </p>
        </div>
        <div className="page-head-actions">
          {canCreate && (
            <button
              type="button"
              className="btn-primary"
              onClick={() => startCreate("role")}
            >
              New role
            </button>
          )}
        </div>
      </header>
      <DataTable
        columns={columns}
        rows={rows}
        getRowKey={(r) => r.roleKey}
        isLoading={roles.isLoading}
        isError={roles.isError}
        emptyMessage="No roles defined yet."
        emptyAction={
          canCreate
            ? { label: "New role", onClick: () => startCreate("role") }
            : undefined
        }
        searchPlaceholder="Search roles…"
        searchValue={q}
        onSearchChange={setQ}
        filters={[
          {
            key: "status",
            label: "Status",
            value: status,
            onChange: setStatus,
            options: [
              { value: "", label: "All statuses" },
              ...GOVERNANCE_STATUSES.map((v) => ({ value: v, label: cap(v) })),
            ],
          },
          {
            key: "privileged",
            label: "Privileged",
            value: privileged,
            onChange: setPrivileged,
            options: [
              { value: "", label: "All privilege levels" },
              { value: "yes", label: "Privileged" },
              { value: "no", label: "Standard" },
            ],
          },
        ]}
        onClearFilters={() => {
          setStatus("");
          setPrivileged("");
        }}
        serverPagination={{
          page,
          pageSize,
          total: roles.data?.total ?? 0,
          onPageChange: setPage,
          onPageSizeChange: setPageSize,
        }}
      />
    </section>
  );
}

export function PermissionsPage() {
  const { appId, startCreate } = useAppContext();
  const canCreate = useCapabilities().can("ManagePermissions", appId);
  const navigate = useNavigate();
  const crumbs = useAppCrumbs();
  const [status, setStatus] = useUrlState("status");
  const [q, setQ] = useUrlState("q");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = usePageSizeState();
  useEffect(() => setPage(1), [q, status, pageSize]);
  const permissions = usePermissionsPaged(appId, {
    page,
    pageSize,
    q: q || undefined,
    status: status || undefined,
  });
  const columns: DataTableColumn<PermissionSummary>[] = [
    {
      key: "key",
      header: "Permission",
      sortValue: (p) => p.permissionKey,
      searchValue: (p) => `${p.permissionKey} ${p.resource} ${p.action}`,
      render: (p) => (
        <button
          type="button"
          className="link-cell"
          onClick={() => navigate(appPaths.permission(appId, p.permissionKey))}
        >
          <RiskDot level={p.riskLevel} /> {p.permissionKey}
        </button>
      ),
    },
    {
      key: "resource",
      header: "Resource",
      sortValue: (p) => p.resource,
      render: (p) => <code>{p.resource}</code>,
    },
    {
      key: "action",
      header: "Action",
      sortValue: (p) => p.action,
      render: (p) => <code>{p.action}</code>,
    },
    {
      key: "status",
      header: "Status",
      sortValue: (p) => p.status,
      render: (p) => <StatusChip value={p.status} />,
    },
  ];
  const rows = permissions.data?.items ?? [];
  return (
    <section className="page">
      <Breadcrumbs items={crumbs({ label: "Permissions" })} />
      <header className="page-head">
        <div className="page-head-titles">
          <h1>Permissions</h1>
          <p className="page-sub">
            Discrete actions on resources that roles and policies govern.
          </p>
        </div>
        <div className="page-head-actions">
          {canCreate && (
            <button
              type="button"
              className="btn-primary"
              onClick={() => startCreate("permission")}
            >
              New permission
            </button>
          )}
        </div>
      </header>
      <DataTable
        columns={columns}
        rows={rows}
        getRowKey={(p) => p.permissionKey}
        isLoading={permissions.isLoading}
        isError={permissions.isError}
        emptyMessage="No permissions defined yet."
        emptyAction={
          canCreate
            ? {
                label: "New permission",
                onClick: () => startCreate("permission"),
              }
            : undefined
        }
        searchPlaceholder="Search permissions…"
        searchValue={q}
        onSearchChange={setQ}
        filters={[
          {
            key: "status",
            label: "Status",
            value: status,
            onChange: setStatus,
            options: [
              { value: "", label: "All statuses" },
              ...GOVERNANCE_STATUSES.map((v) => ({ value: v, label: cap(v) })),
            ],
          },
        ]}
        onClearFilters={() => setStatus("")}
        serverPagination={{
          page,
          pageSize,
          total: permissions.data?.total ?? 0,
          onPageChange: setPage,
          onPageSizeChange: setPageSize,
        }}
      />
    </section>
  );
}

export function PoliciesPage() {
  const { appId, startCreate } = useAppContext();
  const canCreate = useCapabilities().can("ManagePolicies", appId);
  const navigate = useNavigate();
  const crumbs = useAppCrumbs();
  const [effect, setEffect] = useUrlState("effect");
  const [state, setState] = useUrlState("state");
  const [q, setQ] = useUrlState("q");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = usePageSizeState();
  useEffect(() => setPage(1), [q, effect, state, pageSize]);
  const policies = usePoliciesPaged(appId, {
    page,
    pageSize,
    q: q || undefined,
    effect: effect || undefined,
    state: state || undefined,
  });
  const columns: DataTableColumn<PolicySummary>[] = [
    {
      key: "key",
      header: "Policy",
      sortValue: (p) => p.policyKey,
      searchValue: (p) => `${p.policyKey} ${p.permissionKey}`,
      render: (p) => (
        <button
          type="button"
          className="link-cell"
          onClick={() => navigate(appPaths.policy(appId, p.policyKey))}
        >
          <span
            className={`effect-pip effect-${p.effect.toLowerCase()}`}
            aria-hidden="true"
          />{" "}
          {p.policyKey}
        </button>
      ),
    },
    {
      key: "permission",
      header: "Permission",
      sortValue: (p) => p.permissionKey,
      render: (p) => <code>{p.permissionKey}</code>,
    },
    {
      key: "effect",
      header: "Effect",
      sortValue: (p) => p.effect,
      render: (p) => p.effect,
    },
    {
      key: "priority",
      header: "Priority",
      sortValue: (p) => p.priority ?? 0,
      render: (p) => <span className="priority-cell">{p.priority ?? 0}</span>,
    },
    {
      key: "state",
      header: "State",
      sortValue: (p) => p.state,
      render: (p) => <StateBadge value={p.state} />,
    },
  ];
  const rows = policies.data?.items ?? [];
  return (
    <section className="page">
      <Breadcrumbs items={crumbs({ label: "Policies" })} />
      <header className="page-head">
        <div className="page-head-titles">
          <h1>Policies</h1>
          <p className="page-sub">
            Conditional allow or deny rules evaluated against a permission.
          </p>
        </div>
        <div className="page-head-actions">
          {canCreate && (
            <button
              type="button"
              className="btn-primary"
              onClick={() => startCreate("policy")}
            >
              New policy
            </button>
          )}
        </div>
      </header>
      <DataTable
        columns={columns}
        rows={rows}
        getRowKey={(p) => p.policyKey}
        isLoading={policies.isLoading}
        isError={policies.isError}
        emptyMessage="No policies defined yet."
        emptyAction={
          canCreate
            ? { label: "New policy", onClick: () => startCreate("policy") }
            : undefined
        }
        searchPlaceholder="Search policies…"
        searchValue={q}
        onSearchChange={setQ}
        filters={[
          {
            key: "effect",
            label: "Effect",
            value: effect,
            onChange: setEffect,
            options: [
              { value: "", label: "All effects" },
              ...POLICY_EFFECTS.map((v) => ({ value: v, label: cap(v) })),
            ],
          },
          {
            key: "state",
            label: "State",
            value: state,
            onChange: setState,
            options: [
              { value: "", label: "All states" },
              ...POLICY_STATES.map((v) => ({ value: v, label: cap(v) })),
            ],
          },
        ]}
        onClearFilters={() => {
          setEffect("");
          setState("");
        }}
        serverPagination={{
          page,
          pageSize,
          total: policies.data?.total ?? 0,
          onPageChange: setPage,
          onPageSizeChange: setPageSize,
        }}
      />
    </section>
  );
}

export function ReferenceDataPage() {
  const { appId } = useAppContext();
  const canManage = useCapabilities().can("ManagePolicies", appId);
  const crumbs = useAppCrumbs();
  const [status, setStatus] = useUrlState("status");
  const [q, setQ] = useUrlState("q");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = usePageSizeState();
  useEffect(() => setPage(1), [q, status, pageSize]);
  const refData = useReferenceDataPaged(appId, {
    page,
    pageSize,
    q: q || undefined,
    status: status || undefined,
  });
  const remove = useDeleteReferenceData(appId);
  const [creating, setCreating] = useState(false);
  const [editing, setEditing] = useState<ReferenceDataSummary | null>(null);
  const [confirmArchive, setConfirmArchive] =
    useState<ReferenceDataSummary | null>(null);

  const columns: DataTableColumn<ReferenceDataSummary>[] = [
    {
      key: "key",
      header: "Key",
      sortValue: (r) => r.key,
      searchValue: (r) => `${r.key} ${r.description ?? ""}`,
      render: (r) => (
        <button
          type="button"
          className="link-cell"
          disabled={!canManage}
          onClick={() => canManage && setEditing(r)}
        >
          <code>reference.{r.key}</code>
        </button>
      ),
    },
    {
      key: "description",
      header: "Description",
      sortValue: (r) => r.description ?? "",
      render: (r) =>
        r.description ? r.description : <span className="muted">—</span>,
    },
    {
      key: "value",
      header: "Value",
      render: (r) => (
        <code className="refdata-preview">{valuePreview(r.value)}</code>
      ),
    },
    {
      key: "status",
      header: "Status",
      sortValue: (r) => r.status,
      render: (r) => <StatusChip value={r.status} />,
    },
  ];
  const rows = refData.data?.items ?? [];
  return (
    <section className="page">
      <Breadcrumbs items={crumbs({ label: "Reference data" })} />
      <header className="page-head">
        <div className="page-head-titles">
          <h1>Reference data</h1>
          <p className="page-sub">
            Named JSON documents referenced by policy conditions via{" "}
            <code>reference.&lt;key&gt;</code> — maintain shared lookup values in
            one place.
          </p>
        </div>
        <div className="page-head-actions">
          {canManage && (
            <button
              type="button"
              className="btn-primary"
              onClick={() => setCreating(true)}
            >
              New reference data
            </button>
          )}
        </div>
      </header>
      <DataTable
        columns={columns}
        rows={rows}
        getRowKey={(r) => r.key}
        isLoading={refData.isLoading}
        isError={refData.isError}
        emptyMessage="No reference data defined yet."
        emptyAction={
          canManage
            ? { label: "New reference data", onClick: () => setCreating(true) }
            : undefined
        }
        searchPlaceholder="Search reference data…"
        searchValue={q}
        onSearchChange={setQ}
        filters={[
          {
            key: "status",
            label: "Status",
            value: status,
            onChange: setStatus,
            options: [
              { value: "", label: "All statuses" },
              { value: "ACTIVE", label: "Active" },
              { value: "ARCHIVED", label: "Archived" },
            ],
          },
        ]}
        onClearFilters={() => setStatus("")}
        rowActions={
          canManage
            ? (r) => (
                <div className="row-actions">
                  <button
                    type="button"
                    className="mini-btn"
                    onClick={() => setEditing(r)}
                  >
                    Edit
                  </button>
                  {r.status !== "ARCHIVED" && (
                    <button
                      type="button"
                      className="mini-btn danger"
                      onClick={() => setConfirmArchive(r)}
                    >
                      Archive
                    </button>
                  )}
                </div>
              )
            : undefined
        }
        serverPagination={{
          page,
          pageSize,
          total: refData.data?.total ?? 0,
          onPageChange: setPage,
          onPageSizeChange: setPageSize,
        }}
      />
      {creating && (
        <ReferenceDataForm appId={appId} onClose={() => setCreating(false)} />
      )}
      {editing && (
        <ReferenceDataForm
          appId={appId}
          existing={editing}
          onClose={() => setEditing(null)}
        />
      )}
      <ConfirmDialog
        open={!!confirmArchive}
        danger
        title={`Archive ${confirmArchive?.key ?? ""}?`}
        message="The runtime engine stops resolving this reference immediately. History is preserved and you can restore it by editing."
        confirmLabel={remove.isPending ? "Archiving…" : "Archive"}
        onConfirm={() => {
          if (!confirmArchive) return;
          remove.mutate(confirmArchive.key, {
            onSuccess: () => setConfirmArchive(null),
          });
        }}
        onCancel={() => setConfirmArchive(null)}
      />
    </section>
  );
}

// Compact single-line preview of a reference-data JSON value for the table cell.
function valuePreview(value: string): string {
  const collapsed = value.replace(/\s+/g, " ").trim();
  return collapsed.length > 60 ? `${collapsed.slice(0, 60)}…` : collapsed;
}

export function RoleDetailPage() {
  const { appId } = useAppContext();
  const { roleKey = "" } = useParams();
  const { go } = useAppNav();
  const crumbs = useAppCrumbs();
  const roles = useRoles(appId);
  if (roles.isLoading) return <Spinner label="Loading role…" />;
  if (!roles.data?.some((r) => r.roleKey === roleKey))
    return (
      <EmptyBlock title="Role not found" hint="It may have been removed." />
    );
  return (
    <>
      <Breadcrumbs
        items={crumbs(
          { label: "Roles", to: appPaths.roles(appId) },
          { label: roleKey },
        )}
      />
      <Inspector
        appId={appId}
        selection={{ kind: "role", key: roleKey }}
        onSelect={go}
      />
    </>
  );
}

export function PermissionDetailPage() {
  const { appId } = useAppContext();
  const { permissionKey = "" } = useParams();
  const { go } = useAppNav();
  const crumbs = useAppCrumbs();
  const permissions = usePermissions(appId);
  if (permissions.isLoading) return <Spinner label="Loading permission…" />;
  if (!permissions.data?.some((p) => p.permissionKey === permissionKey))
    return (
      <EmptyBlock
        title="Permission not found"
        hint="It may have been removed."
      />
    );
  return (
    <>
      <Breadcrumbs
        items={crumbs(
          { label: "Permissions", to: appPaths.permissions(appId) },
          { label: permissionKey },
        )}
      />
      <Inspector
        appId={appId}
        selection={{ kind: "permission", key: permissionKey }}
        onSelect={go}
      />
    </>
  );
}

export function PolicyDetailPage() {
  const { appId } = useAppContext();
  const { policyKey = "" } = useParams();
  const { go } = useAppNav();
  const crumbs = useAppCrumbs();
  const policies = usePolicies(appId);
  if (policies.isLoading) return <Spinner label="Loading policy…" />;
  if (!policies.data?.some((p) => p.policyKey === policyKey))
    return (
      <EmptyBlock title="Policy not found" hint="It may have been removed." />
    );
  return (
    <>
      <Breadcrumbs
        items={crumbs(
          { label: "Policies", to: appPaths.policies(appId) },
          { label: policyKey },
        )}
      />
      <Inspector
        appId={appId}
        selection={{ kind: "policy", key: policyKey }}
        onSelect={go}
      />
    </>
  );
}

export function MatrixPage() {
  const { appId } = useAppContext();
  const { go } = useAppNav();
  const crumbs = useAppCrumbs();
  return (
    <section className="page">
      <Breadcrumbs items={crumbs({ label: "Matrix" })} />
      <AccessMatrix appId={appId} onSelect={go} />
    </section>
  );
}

export function AssignmentsPage() {
  const { appId } = useAppContext();
  const crumbs = useAppCrumbs();
  return (
    <section className="page">
      <Breadcrumbs items={crumbs({ label: "Assignments" })} />
      <AccessPanel appId={appId} />
    </section>
  );
}

export function IdentityPage() {
  const { appId } = useAppContext();
  return <IdentityPanel appId={appId} />;
}

export function SimulatorPage() {
  const { appId } = useAppContext();
  return <SimulatorPanel appId={appId} />;
}

export function ActivityPage() {
  const { appId } = useAppContext();
  const crumbs = useAppCrumbs();
  return (
    <section className="page">
      <Breadcrumbs items={crumbs({ label: "Activity" })} />
      <ActivityPanel appId={appId} />
    </section>
  );
}

export function DecisionsPage() {
  const { appId } = useAppContext();
  const crumbs = useAppCrumbs();
  return (
    <section className="page">
      <Breadcrumbs items={crumbs({ label: "Decisions" })} />
      <DecisionAnalytics appId={appId} />
    </section>
  );
}

export function CertificationsPage() {
  const { appId } = useAppContext();
  const crumbs = useAppCrumbs();
  return (
    <section className="page">
      <Breadcrumbs items={crumbs({ label: "Certifications" })} />
      <Certifications appId={appId} />
    </section>
  );
}

export function AppSettingsPage() {
  const { appId } = useAppContext();
  const { go } = useAppNav();
  const crumbs = useAppCrumbs();
  return (
    <>
      <Breadcrumbs items={crumbs({ label: "Settings" })} />
      <Inspector
        appId={appId}
        selection={{ kind: "application" }}
        onSelect={go}
      />
    </>
  );
}
