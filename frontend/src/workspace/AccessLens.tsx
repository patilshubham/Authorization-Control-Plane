// Access Lens — a cascading relationship workspace for the authorization model.
//
// Instead of a traditional nested tree, the hierarchy flows left-to-right through
// linked columns (Miller columns): Tenant → Application → Role → Permission → Policy.
// Selecting a node in one column reveals its children in the next, so the full
// relationship path stays visible and in-context. Every level is an independently
// scrollable list, app children are lazy-loaded, and the whole surface is keyboard
// navigable. All data is backend-driven — nothing here is hardcoded.

import { useMemo, useRef, useState } from "react";
import {
  useApplications,
  usePermissions,
  usePolicies,
  useRoles,
  useTenants,
} from "../api/hooks";
import type {
  ApplicationSummary,
  PermissionSummary,
  PolicySummary,
  RoleSummary,
  TenantSummary,
} from "../types";
import {
  EmptyBlock,
  RiskDot,
  Segmented,
  Spinner,
} from "../components/primitives";
import { StatusChip } from "../ui";
import { AccessGraph } from "./hierarchy/AccessGraph";

// ── Level iconography (compact inline SVGs, inherit currentColor) ───────────────

type LevelKind = "tenant" | "application" | "role" | "permission" | "policy";

function LensIcon({ kind }: { kind: LevelKind }) {
  const common = {
    width: 16,
    height: 16,
    viewBox: "0 0 16 16",
    fill: "none",
    stroke: "currentColor",
    strokeWidth: 1.4,
    strokeLinecap: "round" as const,
    strokeLinejoin: "round" as const,
    "aria-hidden": true,
  };
  switch (kind) {
    case "tenant":
      return (
        <svg {...common}>
          <path d="M2.5 13.5h11" />
          <path d="M3.5 13.5V4l4.5-2 4.5 2v9.5" />
          <path d="M6 6.5h1.5M9 6.5h1.5M6 9h1.5M9 9h1.5" />
        </svg>
      );
    case "application":
      return (
        <svg {...common}>
          <path d="M8 1.8 14 5 8 8.2 2 5z" />
          <path d="M2 8l6 3.2L14 8" />
          <path d="M2 11l6 3.2L14 11" />
        </svg>
      );
    case "role":
      return (
        <svg {...common}>
          <path d="M8 1.6 13 3.4v4.2c0 3.2-2.1 5.4-5 6.8-2.9-1.4-5-3.6-5-6.8V3.4z" />
          <path d="M5.8 7.9 7.4 9.5 10.4 6" />
        </svg>
      );
    case "permission":
      return (
        <svg {...common}>
          <circle cx="5.4" cy="5.4" r="2.6" />
          <path d="M7.3 7.3 13 13M11 11l1.6-1.6M9.4 9.4 11 7.8" />
        </svg>
      );
    case "policy":
      return (
        <svg {...common}>
          <path d="M4 2h6l2.5 2.5V14H4z" />
          <path d="M9.6 2v3H12.5" />
          <path d="M6 8h4M6 10.5h4" />
        </svg>
      );
  }
}

// ── Match / highlight helpers ───────────────────────────────────────────────────

function matches(q: string, ...values: (string | null | undefined)[]): boolean {
  if (!q) return true;
  return values.some((v) => (v ?? "").toLowerCase().includes(q));
}

function Highlight({ text, q }: { text: string; q: string }) {
  if (!q) return <>{text}</>;
  const i = text.toLowerCase().indexOf(q);
  if (i < 0) return <>{text}</>;
  return (
    <>
      {text.slice(0, i)}
      <mark className="lens-hl">{text.slice(i, i + q.length)}</mark>
      {text.slice(i + q.length)}
    </>
  );
}

// ── Column + node primitives ────────────────────────────────────────────────────

function LensColumn({
  kind,
  title,
  count,
  index,
  children,
  loading,
  emptyLabel,
  isEmpty,
}: {
  kind: LevelKind;
  title: string;
  count?: number;
  index: number;
  children: React.ReactNode;
  loading?: boolean;
  emptyLabel?: string;
  isEmpty?: boolean;
}) {
  return (
    <section
      className={`lens-col lens-col-${kind}`}
      style={{ animationDelay: `${index * 45}ms` }}
      aria-label={title}
    >
      <header className="lens-col-head">
        <span className="lens-col-icon">
          <LensIcon kind={kind} />
        </span>
        <span className="lens-col-title">{title}</span>
        {typeof count === "number" && (
          <span className="lens-col-count">{count}</span>
        )}
      </header>
      <div
        className="lens-col-body"
        role="listbox"
        aria-label={title}
        data-lens-col={index}
        tabIndex={-1}
      >
        {loading ? (
          <div className="lens-col-loading">
            <Spinner label="Loading…" />
          </div>
        ) : isEmpty ? (
          <p className="lens-empty">{emptyLabel}</p>
        ) : (
          children
        )}
      </div>
    </section>
  );
}

function LensNode({
  kind,
  name,
  subtitle,
  q,
  selected,
  tabbable,
  privileged,
  riskLevel,
  status,
  effect,
  childCount,
  childLabel,
  onClick,
}: {
  kind: LevelKind;
  name: string;
  subtitle?: string;
  q: string;
  selected: boolean;
  tabbable: boolean;
  privileged?: boolean;
  riskLevel?: string;
  status?: string;
  effect?: string;
  childCount?: number;
  childLabel?: string;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      role="option"
      aria-selected={selected}
      tabIndex={tabbable ? 0 : -1}
      data-lens-node
      className={`lens-node${selected ? " is-selected" : ""}`}
      onClick={onClick}
    >
      <span className={`lens-node-icon lens-node-icon-${kind}`}>
        {effect ? (
          <span
            className={`effect-pip effect-${effect.toLowerCase()}`}
            aria-hidden="true"
          />
        ) : (
          <LensIcon kind={kind} />
        )}
        {riskLevel && <RiskDot level={riskLevel} />}
      </span>
      <span className="lens-node-main">
        <span className="lens-node-name">
          <Highlight text={name} q={q} />
          {privileged && (
            <span className="lens-star" title="Privileged">
              ★
            </span>
          )}
        </span>
        {subtitle && (
          <span className="lens-node-key">
            <Highlight text={subtitle} q={q} />
          </span>
        )}
      </span>
      <span className="lens-node-badges">
        {status && <StatusChip value={status} />}
        {typeof childCount === "number" && (
          <span
            className="lens-count"
            title={`${childCount} ${childLabel ?? ""}`}
          >
            {childCount}
          </span>
        )}
      </span>
      {typeof childCount === "number" && childCount > 0 && (
        <span className="lens-node-chevron" aria-hidden="true">
          ›
        </span>
      )}
    </button>
  );
}

// ── Roles / Permissions / Policies columns (lazy — mounted only per selected app) ─

function AppColumns({
  appId,
  q,
  roleKey,
  permissionKey,
  onSelectRole,
  onSelectPermission,
}: {
  appId: string;
  q: string;
  roleKey?: string;
  permissionKey?: string;
  onSelectRole: (key: string) => void;
  onSelectPermission: (key: string) => void;
}) {
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

  const roleList = (roles.data ?? []).filter((r) =>
    matches(q, r.name, r.roleKey),
  );
  const selectedRole = (roles.data ?? []).find((r) => r.roleKey === roleKey);

  // Permissions shown are those granted by the selected role (the meaningful relationship).
  const rolePermissions: PermissionSummary[] = useMemo(() => {
    if (!selectedRole) return [];
    return selectedRole.permissions
      .map((key) => permByKey.get(key))
      .filter((p): p is PermissionSummary => !!p)
      .filter((p) => matches(q, p.permissionKey, p.resource, p.action));
  }, [selectedRole, permByKey, q]);

  const governingPolicies = useMemo(() => {
    if (!permissionKey) return [];
    return (policiesByPermission.get(permissionKey) ?? []).filter((p) =>
      matches(q, p.policyKey, p.effect),
    );
  }, [policiesByPermission, permissionKey, q]);

  return (
    <>
      <LensColumn
        kind="role"
        title="Roles"
        index={2}
        count={roles.data?.length}
        loading={roles.isLoading}
        isEmpty={!roles.isLoading && roleList.length === 0}
        emptyLabel={
          q
            ? "No roles match your search."
            : "No roles defined for this application."
        }
      >
        {roleList.map((role: RoleSummary, i) => (
          <LensNode
            key={role.roleKey}
            kind="role"
            name={role.name}
            subtitle={role.roleKey}
            q={q}
            selected={role.roleKey === roleKey}
            tabbable={role.roleKey === roleKey || (i === 0 && !roleKey)}
            privileged={role.privileged}
            riskLevel={role.riskLevel}
            status={role.status}
            childCount={role.permissions.length}
            childLabel="permissions"
            onClick={() => onSelectRole(role.roleKey)}
          />
        ))}
      </LensColumn>

      <LensColumn
        kind="permission"
        title="Permissions"
        index={3}
        count={selectedRole ? selectedRole.permissions.length : undefined}
        loading={permissions.isLoading && !!roleKey}
        isEmpty={!!selectedRole && rolePermissions.length === 0}
        emptyLabel={
          !selectedRole
            ? "Select a role to see the permissions it grants."
            : q
              ? "No granted permissions match your search."
              : "This role grants no permissions yet."
        }
      >
        {selectedRole &&
          rolePermissions.map((perm, i) => (
            <LensNode
              key={perm.permissionKey}
              kind="permission"
              name={perm.permissionKey}
              subtitle={`${perm.resource} · ${perm.action}`}
              q={q}
              selected={perm.permissionKey === permissionKey}
              tabbable={
                perm.permissionKey === permissionKey ||
                (i === 0 && !permissionKey)
              }
              riskLevel={perm.riskLevel}
              status={perm.status}
              childCount={
                (policiesByPermission.get(perm.permissionKey) ?? []).length
              }
              childLabel="policies"
              onClick={() => onSelectPermission(perm.permissionKey)}
            />
          ))}
      </LensColumn>

      <LensColumn
        kind="policy"
        title="Policies"
        index={4}
        count={permissionKey ? governingPolicies.length : undefined}
        loading={policies.isLoading && !!permissionKey}
        isEmpty={!!permissionKey && governingPolicies.length === 0}
        emptyLabel={
          !permissionKey
            ? "Select a permission to see the policies that govern it."
            : "No policies govern this permission."
        }
      >
        {permissionKey &&
          governingPolicies.map((pol, i) => (
            <LensNode
              key={pol.policyKey}
              kind="policy"
              name={pol.policyKey}
              subtitle={`${pol.effect} · ${pol.state}`}
              q={q}
              selected={false}
              tabbable={i === 0}
              effect={pol.effect}
              status={pol.state}
              onClick={() => onSelectPermission(permissionKey)}
            />
          ))}
      </LensColumn>
    </>
  );
}

// ── Main surface ──────────────────────────────────────────────────────────────

type LensSelection = {
  tenantId?: string;
  appId?: string;
  roleKey?: string;
  permissionKey?: string;
};

const UNASSIGNED = "__none__";

export function HierarchyExplorer({
  onSelectApp,
}: {
  onSelectApp: (appId: string) => void;
}) {
  const tenants = useTenants();
  const applications = useApplications();
  const [query, setQuery] = useState("");
  const [sel, setSel] = useState<LensSelection>({});
  const [view, setView] = useState<"columns" | "graph">("columns");
  const lensRef = useRef<HTMLDivElement>(null);

  const q = query.trim().toLowerCase();

  const appsByTenant = useMemo(() => {
    const map = new Map<string, ApplicationSummary[]>();
    for (const app of applications.data ?? []) {
      const key = app.tenantId ?? UNASSIGNED;
      if (!map.has(key)) map.set(key, []);
      map.get(key)!.push(app);
    }
    return map;
  }, [applications.data]);

  const tenantRows = useMemo(() => {
    const rows: {
      tenantId: string;
      name: string;
      status: string;
      apps: ApplicationSummary[];
    }[] = (tenants.data ?? []).map((t: TenantSummary) => ({
      tenantId: t.tenantId,
      name: t.name,
      status: t.status,
      apps: appsByTenant.get(t.tenantId) ?? [],
    }));
    if (appsByTenant.has(UNASSIGNED)) {
      rows.push({
        tenantId: UNASSIGNED,
        name: "Unassigned",
        status: "—",
        apps: appsByTenant.get(UNASSIGNED)!,
      });
    }
    return rows.filter(
      (r) =>
        matches(q, r.name, r.tenantId) ||
        r.apps.some((a) => matches(q, a.name, a.applicationId)),
    );
  }, [tenants.data, appsByTenant, q]);

  const selectedTenant = tenantRows.find((t) => t.tenantId === sel.tenantId);
  const tenantApps = (selectedTenant?.apps ?? []).filter((a) =>
    matches(q, a.name, a.applicationId),
  );

  const selectTenant = (tenantId: string) => setSel({ tenantId });
  const selectApp = (appId: string) =>
    setSel((s) => ({ tenantId: s.tenantId, appId }));
  const selectRole = (roleKey: string) =>
    setSel((s) => ({ ...s, roleKey, permissionKey: undefined }));
  const selectPermission = (permissionKey: string) =>
    setSel((s) => ({ ...s, permissionKey }));

  const currentApp = (applications.data ?? []).find(
    (a) => a.applicationId === sel.appId,
  );

  // Roving keyboard navigation across the columns.
  const onKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    const active = document.activeElement as HTMLElement | null;
    if (!active || !active.hasAttribute("data-lens-node") || !lensRef.current)
      return;
    const col = active.closest("[data-lens-col]") as HTMLElement | null;
    if (!col) return;
    const nodes = [...col.querySelectorAll<HTMLElement>("[data-lens-node]")];
    const idx = nodes.indexOf(active);
    const focus = (el?: HTMLElement) => {
      if (el) {
        e.preventDefault();
        el.focus();
      }
    };
    if (e.key === "ArrowDown")
      focus(nodes[Math.min(idx + 1, nodes.length - 1)]);
    else if (e.key === "ArrowUp") focus(nodes[Math.max(idx - 1, 0)]);
    else if (e.key === "Home") focus(nodes[0]);
    else if (e.key === "End") focus(nodes[nodes.length - 1]);
    else if (e.key === "ArrowRight" || e.key === "ArrowLeft") {
      const cols = [
        ...lensRef.current.querySelectorAll<HTMLElement>("[data-lens-col]"),
      ];
      const target =
        cols[cols.indexOf(col) + (e.key === "ArrowRight" ? 1 : -1)];
      if (target)
        focus(
          target.querySelector<HTMLElement>(
            '[data-lens-node][aria-selected="true"]',
          ) ??
            target.querySelector<HTMLElement>("[data-lens-node]") ??
            undefined,
        );
    }
  };

  if (tenants.isLoading || applications.isLoading)
    return <Spinner label="Loading hierarchy…" />;

  const crumbs = [
    selectedTenant && {
      label: selectedTenant.name,
      onClick: () => setSel({ tenantId: selectedTenant.tenantId }),
    },
    currentApp && {
      label: currentApp.name,
      onClick: () => setSel({ tenantId: sel.tenantId, appId: sel.appId }),
    },
    sel.roleKey && {
      label: sel.roleKey,
      onClick: () => setSel((s) => ({ ...s, permissionKey: undefined })),
    },
    sel.permissionKey && { label: sel.permissionKey, onClick: () => undefined },
  ].filter(Boolean) as { label: string; onClick: () => void }[];

  return (
    <article className="lens-wrap">
      <header className="lens-header">
        <div className="lens-header-top">
          <div>
            <h1 className="lens-title">Access lens</h1>
            <p className="lens-subtitle">
              Follow access across the model — Tenant → Application → Role →
              Permission → Policy.
            </p>
          </div>
          <div className="lens-header-actions">
            <Segmented
              ariaLabel="View mode"
              value={view}
              onChange={setView}
              options={[
                { value: "columns", label: "Columns" },
                { value: "graph", label: "Graph" },
              ]}
            />
            <input
              type="search"
              className="lens-search"
              placeholder="Search across every level…"
              aria-label="Search hierarchy"
              value={query}
              onChange={(e) => setQuery(e.target.value)}
            />
          </div>
        </div>
        <nav className="lens-breadcrumb" aria-label="Selected path">
          <button
            type="button"
            className="lens-crumb"
            onClick={() => setSel({})}
          >
            All tenants
          </button>
          {crumbs.map((c, i) => (
            <span className="lens-crumb-group" key={i}>
              <span className="lens-crumb-sep" aria-hidden="true">
                ›
              </span>
              <button type="button" className="lens-crumb" onClick={c.onClick}>
                {c.label}
              </button>
            </span>
          ))}
          {currentApp && (
            <button
              type="button"
              className="lens-open-app"
              onClick={() => onSelectApp(currentApp.applicationId)}
            >
              Open {currentApp.name} →
            </button>
          )}
        </nav>
      </header>

      <div className="lens" ref={lensRef} onKeyDown={onKeyDown}>
        {view === "graph" ? (
          <AccessGraph
            tenantRows={tenantRows}
            selection={sel}
            q={q}
            onSelectTenant={selectTenant}
            onSelectApp={selectApp}
            onSelectRole={selectRole}
            onSelectPermission={selectPermission}
          />
        ) : (
          <>
            <LensColumn
              kind="tenant"
              title="Tenants"
              index={0}
              count={tenantRows.length}
              isEmpty={tenantRows.length === 0}
              emptyLabel="No tenants match your search."
            >
              {tenantRows.map((t, i) => (
                <LensNode
                  key={t.tenantId}
                  kind="tenant"
                  name={t.name}
                  subtitle={
                    t.tenantId === UNASSIGNED ? "no owning tenant" : t.tenantId
                  }
                  q={q}
                  selected={t.tenantId === sel.tenantId}
                  tabbable={
                    t.tenantId === sel.tenantId || (i === 0 && !sel.tenantId)
                  }
                  status={t.status === "—" ? undefined : t.status}
                  childCount={t.apps.length}
                  childLabel="applications"
                  onClick={() => selectTenant(t.tenantId)}
                />
              ))}
            </LensColumn>

            <LensColumn
              kind="application"
              title="Applications"
              index={1}
              count={selectedTenant ? selectedTenant.apps.length : undefined}
              isEmpty={!!selectedTenant && tenantApps.length === 0}
              emptyLabel={
                !selectedTenant
                  ? "Select a tenant to see its applications."
                  : "No applications match your search."
              }
            >
              {selectedTenant &&
                tenantApps.map((app, i) => (
                  <LensNode
                    key={app.applicationId}
                    kind="application"
                    name={app.name}
                    subtitle={app.applicationId}
                    q={q}
                    selected={app.applicationId === sel.appId}
                    tabbable={
                      app.applicationId === sel.appId || (i === 0 && !sel.appId)
                    }
                    riskLevel={app.riskLevel}
                    status={app.status}
                    onClick={() => selectApp(app.applicationId)}
                  />
                ))}
            </LensColumn>

            {sel.appId ? (
              <AppColumns
                appId={sel.appId}
                q={q}
                roleKey={sel.roleKey}
                permissionKey={sel.permissionKey}
                onSelectRole={selectRole}
                onSelectPermission={selectPermission}
              />
            ) : (
              <>
                <LensColumn
                  kind="role"
                  title="Roles"
                  index={2}
                  isEmpty
                  emptyLabel="Select an application to see its roles."
                >
                  {null}
                </LensColumn>
                <LensColumn
                  kind="permission"
                  title="Permissions"
                  index={3}
                  isEmpty
                  emptyLabel="Select a role to see the permissions it grants."
                >
                  {null}
                </LensColumn>
                <LensColumn
                  kind="policy"
                  title="Policies"
                  index={4}
                  isEmpty
                  emptyLabel="Select a permission to see the policies that govern it."
                >
                  {null}
                </LensColumn>
              </>
            )}
          </>
        )}
      </div>

      {tenantRows.length === 0 && q && (
        <EmptyBlock
          title="Nothing matches"
          hint="Try a different search term."
        />
      )}
    </article>
  );
}
