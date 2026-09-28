import { useMemo, useState } from "react";
import {
  useBulkPublishMappings,
  useGrantPermission,
  useMappings,
  usePermissions,
  usePublishMapping,
  useRoles,
  useUnmapPermission,
} from "../api/hooks";
import type { PermissionSummary, RolePermissionSummary } from "../types";
import { EmptyBlock, RiskDot, Spinner } from "../components/primitives";
import { ConfirmDialog } from "../ui";
import { AppIcon } from "../components/icons";
import type { Selection } from "./selection";
import { useCapabilities } from "../capabilities";
import { VizModal } from "../components/viz/VizModal";
import {
  D3BipartiteGraph,
  type BipartiteNode,
  type BipartiteLink,
} from "../components/viz/D3BipartiteGraph";

/**
 * Role × Permission workspace. Each cell is a live grant that cycles on click:
 *   empty → grants (draft) → publish → revoke.
 *
 * Permissions are grouped by resource into collapsible column bands so large
 * catalogs stay navigable (progressive disclosure). Sticky corner/header/first
 * column keep orientation, dual search narrows both axes, and per-row/column
 * coverage counts plus a summary strip surface the shape of access at a glance.
 * Everything is backend-driven; the layout scales to wide catalogs via the
 * horizontally scrollable, group-collapsible grid.
 */
type CellState = "published" | "draft" | "empty";

function cellStateOf(cell: RolePermissionSummary | undefined): CellState {
  if (!cell) return "empty";
  return cell.state === "PUBLISHED" ? "published" : "draft";
}

export function AccessMatrix({
  appId,
  onSelect,
}: {
  appId: string;
  onSelect: (s: Selection) => void;
}) {
  const roles = useRoles(appId);
  const permissions = usePermissions(appId);
  const mappings = useMappings(appId);
  const grant = useGrantPermission(appId);
  const publish = usePublishMapping(appId);
  const unmap = useUnmapPermission(appId);
  const bulkPublish = useBulkPublishMappings(appId);
  const canMapRolePermission = useCapabilities().can(
    "MapRolePermission",
    appId,
  );

  const [roleFilter, setRoleFilter] = useState("");
  const [permFilter, setPermFilter] = useState("");
  const [resourceFilter, setResourceFilter] = useState("");
  const [privilegedFilter, setPrivilegedFilter] = useState("");
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(
    () => new Set(),
  );
  const [graphOpen, setGraphOpen] = useState(false);
  const [confirmPublishAll, setConfirmPublishAll] = useState(false);

  const cellIndex = useMemo(() => {
    const map = new Map<string, RolePermissionSummary>();
    for (const m of mappings.data ?? [])
      map.set(`${m.roleKey}::${m.permissionKey}`, m);
    return map;
  }, [mappings.data]);

  const resources = useMemo(
    () =>
      Array.from(
        new Set(
          (permissions.data ?? []).map((p) => p.resource).filter(Boolean),
        ),
      ).sort(),
    [permissions.data],
  );

  const rq = roleFilter.trim().toLowerCase();
  const pq = permFilter.trim().toLowerCase();

  const visibleRoles = useMemo(
    () =>
      (roles.data ?? []).filter(
        (r) =>
          (!rq ||
            r.name.toLowerCase().includes(rq) ||
            r.roleKey.toLowerCase().includes(rq)) &&
          (!privilegedFilter || (privilegedFilter === "yes") === r.privileged),
      ),
    [roles.data, rq, privilegedFilter],
  );

  // Group visible permissions by resource; resources and their actions sorted
  // for a stable, scannable layout.
  const groups = useMemo(() => {
    const visible = (permissions.data ?? []).filter(
      (p) =>
        (!resourceFilter || p.resource === resourceFilter) &&
        (!pq ||
          p.permissionKey.toLowerCase().includes(pq) ||
          p.resource.toLowerCase().includes(pq) ||
          p.action.toLowerCase().includes(pq)),
    );
    const byResource = new Map<string, PermissionSummary[]>();
    for (const p of visible) {
      const key = p.resource || "—";
      const bucket = byResource.get(key);
      if (bucket) bucket.push(p);
      else byResource.set(key, [p]);
    }
    return [...byResource.entries()]
      .map(([resource, perms]) => ({
        resource,
        perms: perms
          .slice()
          .sort(
            (a, b) =>
              a.action.localeCompare(b.action) ||
              a.permissionKey.localeCompare(b.permissionKey),
          ),
      }))
      .sort((a, b) => a.resource.localeCompare(b.resource));
  }, [permissions.data, pq, resourceFilter]);

  const visiblePermKeys = useMemo(
    () => new Set(groups.flatMap((g) => g.perms.map((p) => p.permissionKey))),
    [groups],
  );

  // Per-role granted count (published + draft) across visible permissions.
  const roleGranted = useMemo(() => {
    const map = new Map<string, number>();
    for (const r of visibleRoles) {
      let n = 0;
      for (const key of visiblePermKeys)
        if (cellIndex.has(`${r.roleKey}::${key}`)) n++;
      map.set(r.roleKey, n);
    }
    return map;
  }, [visibleRoles, visiblePermKeys, cellIndex]);

  // Per-permission granted role count across visible roles.
  const permGranted = useMemo(() => {
    const map = new Map<string, number>();
    for (const g of groups)
      for (const p of g.perms) {
        let n = 0;
        for (const r of visibleRoles)
          if (cellIndex.has(`${r.roleKey}::${p.permissionKey}`)) n++;
        map.set(p.permissionKey, n);
      }
    return map;
  }, [groups, visibleRoles, cellIndex]);

  const totals = useMemo(() => {
    let published = 0;
    let draft = 0;
    for (const r of visibleRoles)
      for (const key of visiblePermKeys) {
        const c = cellIndex.get(`${r.roleKey}::${key}`);
        if (!c) continue;
        if (c.state === "PUBLISHED") published++;
        else draft++;
      }
    return { published, draft };
  }, [visibleRoles, visiblePermKeys, cellIndex]);

  const busy =
    grant.isPending ||
    publish.isPending ||
    unmap.isPending ||
    !canMapRolePermission;

  // Every draft grant across the app (not just the filtered view) so "publish all
  // drafts" clears the full backlog in one audited batch.
  const draftIds = useMemo(
    () =>
      (mappings.data ?? [])
        .filter((m) => m.state !== "PUBLISHED")
        .map((m) => m.id),
    [mappings.data],
  );

  const onCell = (roleKey: string, permissionKey: string) => {
    const existing = cellIndex.get(`${roleKey}::${permissionKey}`);
    if (!existing) grant.mutate({ roleKey, permissionKey });
    else if (existing.state !== "PUBLISHED") publish.mutate(existing.id);
    else unmap.mutate(existing.id);
  };

  const toggleGroup = (resource: string) =>
    setCollapsed((prev) => {
      const next = new Set(prev);
      if (next.has(resource)) next.delete(resource);
      else next.add(resource);
      return next;
    });

  const visiblePerms = useMemo(() => groups.flatMap((g) => g.perms), [groups]);
  const graphLeft = useMemo<BipartiteNode[]>(
    () =>
      visibleRoles.map((r) => ({
        id: r.roleKey,
        label: r.name,
        sublabel: r.roleKey,
        kind: "role",
        riskLevel: r.riskLevel,
      })),
    [visibleRoles],
  );
  const graphRight = useMemo<BipartiteNode[]>(
    () =>
      visiblePerms.map((p) => ({
        id: p.permissionKey,
        label: p.permissionKey,
        sublabel: `${p.resource} · ${p.action}`,
        kind: "permission",
        riskLevel: p.riskLevel,
      })),
    [visiblePerms],
  );
  const graphLinks = useMemo<BipartiteLink[]>(() => {
    const links: BipartiteLink[] = [];
    for (const r of visibleRoles) {
      for (const p of visiblePerms) {
        const cell = cellIndex.get(`${r.roleKey}::${p.permissionKey}`);
        if (cell)
          links.push({
            source: r.roleKey,
            target: p.permissionKey,
            state: cell.state === "PUBLISHED" ? "published" : "draft",
          });
      }
    }
    return links;
  }, [visibleRoles, visiblePerms, cellIndex]);

  if (roles.isLoading || permissions.isLoading)
    return <Spinner label="Loading matrix…" />;
  if (
    (roles.data?.length ?? 0) === 0 ||
    (permissions.data?.length ?? 0) === 0
  ) {
    return (
      <EmptyBlock
        title="Matrix needs roles and permissions"
        hint="Create at least one role and one permission to map access."
      />
    );
  }

  const totalCells = visibleRoles.length * visiblePermKeys.size;
  const coverage = totalCells
    ? Math.round(((totals.published + totals.draft) / totalCells) * 100)
    : 0;
  const noMatches = visibleRoles.length === 0 || groups.length === 0;

  return (
    <div className="matrix-wrap">
      <header className="page-head">
        <div className="page-head-titles">
          <h1>Access matrix</h1>
          <p className="page-sub">
            Map permissions to roles — click a cell to grant, publish or revoke.
          </p>
        </div>
        <div
          className="page-head-actions matrix-summary"
          role="group"
          aria-label="Matrix summary"
        >
          <span className="matrix-stat">
            <strong>{totals.published}</strong> published
          </span>
          <span className="matrix-stat">
            <strong>{totals.draft}</strong> draft
          </span>
          <span className="matrix-stat">
            <strong>{coverage}%</strong> coverage
          </span>
          {canMapRolePermission && draftIds.length > 0 && (
            <button
              type="button"
              className="btn-secondary btn-sm"
              disabled={bulkPublish.isPending}
              onClick={() => setConfirmPublishAll(true)}
            >
              {bulkPublish.isPending
                ? "Publishing…"
                : `Publish ${draftIds.length} draft${draftIds.length === 1 ? "" : "s"}`}
            </button>
          )}
          <button
            type="button"
            className="btn-secondary btn-sm"
            onClick={() => setGraphOpen(true)}
          >
            Coverage graph
          </button>
        </div>
      </header>

      <ConfirmDialog
        open={confirmPublishAll}
        title="Publish all draft grants?"
        message={`Publish ${draftIds.length} draft role→permission grant${draftIds.length === 1 ? "" : "s"}? They take effect immediately for runtime decisions.`}
        confirmLabel={bulkPublish.isPending ? "Publishing…" : "Publish all"}
        confirmDisabled={bulkPublish.isPending}
        onConfirm={() =>
          bulkPublish.mutate(draftIds, {
            onSuccess: () => setConfirmPublishAll(false),
          })
        }
        onCancel={() => setConfirmPublishAll(false)}
      />

      <div className="table-toolbar-wrap">
        <div className="table-toolbar matrix-toolbar">
          <input
            type="search"
            className="table-search"
            placeholder="Filter roles…"
            aria-label="Filter roles"
            value={roleFilter}
            onChange={(e) => setRoleFilter(e.target.value)}
          />
          <input
            type="search"
            className="table-search"
            placeholder="Filter permissions…"
            aria-label="Filter permissions"
            value={permFilter}
            onChange={(e) => setPermFilter(e.target.value)}
          />
          <select
            className={`table-filter${resourceFilter ? " is-active" : ""}`}
            value={resourceFilter}
            aria-label="Resource"
            onChange={(e) => setResourceFilter(e.target.value)}
          >
            <option value="">All resources</option>
            {resources.map((r) => (
              <option key={r} value={r}>
                {r}
              </option>
            ))}
          </select>
          <select
            className={`table-filter${privilegedFilter ? " is-active" : ""}`}
            value={privilegedFilter}
            aria-label="Privileged"
            onChange={(e) => setPrivilegedFilter(e.target.value)}
          >
            <option value="">All privilege levels</option>
            <option value="yes">Privileged</option>
            <option value="no">Standard</option>
          </select>
          <span className="matrix-legend">
            <span className="legend-swatch published" /> Published
            <span className="legend-swatch draft" /> Draft
            <span className="legend-swatch empty" /> None
          </span>
        </div>
      </div>

      {noMatches ? (
        <EmptyBlock
          title="No matches"
          hint="Adjust the role or permission filters to see cells."
        />
      ) : (
        <div className="matrix-scroll">
          <table className="matrix" aria-busy={busy}>
            <thead>
              <tr>
                <th className="matrix-corner" scope="col" rowSpan={2}>
                  <span className="matrix-corner-label">Role \ Permission</span>
                </th>
                {groups.map((g) => {
                  const isCollapsed = collapsed.has(g.resource);
                  return (
                    <th
                      key={g.resource}
                      className="matrix-group"
                      scope="colgroup"
                      colSpan={isCollapsed ? 1 : g.perms.length}
                    >
                      <button
                        type="button"
                        className="matrix-group-toggle"
                        aria-expanded={!isCollapsed}
                        onClick={() => toggleGroup(g.resource)}
                        title={
                          isCollapsed
                            ? `Expand ${g.resource}`
                            : `Collapse ${g.resource}`
                        }
                      >
                        <span
                          className={`matrix-caret${isCollapsed ? "" : " open"}`}
                          aria-hidden="true"
                        >
                          <AppIcon name="chevron" size={12} />
                        </span>
                        <span className="matrix-group-name">{g.resource}</span>
                        <span className="matrix-group-count">
                          {g.perms.length}
                        </span>
                      </button>
                    </th>
                  );
                })}
              </tr>
              <tr>
                {groups.map((g) => {
                  if (collapsed.has(g.resource)) {
                    return (
                      <th
                        key={g.resource}
                        className="matrix-col matrix-col-collapsed"
                        scope="col"
                      >
                        <span>{g.perms.length} perms</span>
                      </th>
                    );
                  }
                  return g.perms.map((p) => (
                    <th
                      key={p.permissionKey}
                      className="matrix-col"
                      scope="col"
                    >
                      <button
                        type="button"
                        className="matrix-colhead"
                        onClick={() =>
                          onSelect({ kind: "permission", key: p.permissionKey })
                        }
                        title={`${p.permissionKey} — granted to ${permGranted.get(p.permissionKey) ?? 0} of ${visibleRoles.length} roles`}
                      >
                        <span className="matrix-colhead-action">
                          {p.action}
                        </span>
                        <span className="matrix-colhead-count">
                          {permGranted.get(p.permissionKey) ?? 0}
                        </span>
                      </button>
                    </th>
                  ));
                })}
              </tr>
            </thead>
            <tbody>
              {visibleRoles.map((role) => (
                <tr key={role.roleKey}>
                  <th className="matrix-row" scope="row">
                    <button
                      type="button"
                      className="matrix-rowhead"
                      onClick={() =>
                        onSelect({ kind: "role", key: role.roleKey })
                      }
                      title={role.name}
                    >
                      <RiskDot level={role.riskLevel} />
                      <span className="matrix-rowhead-name">{role.name}</span>
                      {role.privileged && (
                        <span
                          className="matrix-priv"
                          title="Privileged role"
                          aria-label="Privileged role"
                        >
                          ★
                        </span>
                      )}
                      <span className="matrix-rowhead-count">
                        {roleGranted.get(role.roleKey) ?? 0}
                      </span>
                    </button>
                  </th>
                  {groups.map((g) => {
                    if (collapsed.has(g.resource)) {
                      let granted = 0;
                      for (const p of g.perms)
                        if (
                          cellIndex.has(`${role.roleKey}::${p.permissionKey}`)
                        )
                          granted++;
                      return (
                        <td
                          key={g.resource}
                          className="matrix-cell matrix-cell-collapsed"
                        >
                          <button
                            type="button"
                            className={`matrix-group-cell${granted ? " has-grants" : ""}`}
                            onClick={() => toggleGroup(g.resource)}
                            aria-label={`${role.name}: ${granted} of ${g.perms.length} ${g.resource} permissions granted — expand to edit`}
                          >
                            {granted}/{g.perms.length}
                          </button>
                        </td>
                      );
                    }
                    return g.perms.map((p) => {
                      const cell = cellIndex.get(
                        `${role.roleKey}::${p.permissionKey}`,
                      );
                      const state = cellStateOf(cell);
                      return (
                        <td key={p.permissionKey} className="matrix-cell">
                          <button
                            type="button"
                            className={`matrix-dot ${state}`}
                            disabled={busy}
                            aria-label={`${role.name} → ${p.permissionKey}: ${state}`}
                            title={
                              cell
                                ? `${cell.state} — click to ${cell.state === "PUBLISHED" ? "revoke" : "publish"}`
                                : "Click to grant"
                            }
                            onClick={() =>
                              onCell(role.roleKey, p.permissionKey)
                            }
                          >
                            {state === "published"
                              ? "✓"
                              : state === "draft"
                                ? "•"
                                : ""}
                          </button>
                        </td>
                      );
                    });
                  })}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <VizModal
        title="Coverage graph"
        subtitle="Role ↔ Permission grants"
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
          </>
        }
      >
        <D3BipartiteGraph
          left={graphLeft}
          right={graphRight}
          links={graphLinks}
          leftLabel="Roles"
          rightLabel="Permissions"
          height={640}
          onSelect={(node) => {
            if (node.kind === "role") {
              onSelect({ kind: "role", key: node.id });
              setGraphOpen(false);
            } else if (node.kind === "permission") {
              onSelect({ kind: "permission", key: node.id });
              setGraphOpen(false);
            }
          }}
          ariaLabel="Role and permission coverage graph"
        />
      </VizModal>
    </div>
  );
}
