import { useEffect, useMemo, useState } from "react";
import { Link, useNavigate, useParams } from "react-router-dom";
import {
  useApplications,
  useFilteredApplications,
  useFilteredApplicationsPaged,
  useAuditFeed,
  useAuditActivitySummary,
  usePlatformOverview,
  useTenantDetail,
  useTenants,
  useUsersDirectory,
  useAuditNarrative,
  useAiUsage,
  useAiPromptLogs,
} from "../../api/hooks";
import { EmptyBlock, RiskDot, Spinner } from "../../components/primitives";
import { AppIcon } from "../../components/icons";
import {
  AreaTrend,
  BarDistribution,
  buildActivityTrend,
  buildUsageTrend,
  useChartTheme,
} from "../../components/charts";
import {
  DataTable,
  DrawerPanel,
  FilterBar,
  Pagination,
  StatusChip,
  TabBar,
  usePageSizeState,
  type DataTableColumn,
} from "../../ui";
import { useUrlState } from "../../api/useUrlState";
import { useAiConfig, useAiFeature } from "../../api/aiConfig";
import { getRuntimeConfig } from "../../api/runtimeConfig";
import {
  describeEvent,
  relativeTime,
  dayBucket,
} from "../../workspace/activity";
import { AuditEventCard } from "../../workspace/AuditEventCard";
import { formatDate } from "../../workspace/formatters";
import { AUDIT_CATEGORIES } from "../../constants";
import {
  TenantsPanel,
  UserDetails,
  UsersPanel,
} from "../../workspace/HierarchyPanels";
import { HierarchyExplorer } from "../../workspace/AccessLens";
import { AccessSearch } from "../../workspace/AccessSearch";
import { ApplicationForm } from "../../workspace/CreateForms";
import { VizModal } from "../../components/viz/VizModal";
import { D3Tree } from "../../components/viz/D3Tree";
import { D3Heatmap } from "../../components/viz/D3Heatmap";
import {
  buildTenantAppTree,
  buildTenantSubtree,
  type TenantAppRow,
} from "../../components/viz/graphModel";
import {
  useCapabilities,
  CAPABILITY_ROLES,
  type Capability,
} from "../../capabilities";
import { usePortal } from "../../shells/PortalContext";
import { getDisplayName } from "../../auth";
import { Breadcrumbs } from "../../scope/Scope";
import { appPaths, platformPaths } from "../../workspace/nav";
import type {
  ApplicationSummary,
  TenantApplicationSummary,
  AiFeatureName,
} from "../../types";
import type {
  AuditNarrativeResponse,
  AuditNarrativeEvent,
  AiUsageFeatureCount,
  AiUsageActorCount,
  AiPromptLogItem,
} from "../../apiClient";

const RISK_ORDER = ["CRITICAL", "HIGH", "MEDIUM", "LOW"];

// Friendly labels + reference detail for each AI capability. The overview card
// uses `key` + `label`; the Platform settings "AI features" tab additionally
// surfaces the full detail inside an expandable block per feature:
//  · what   — one plain-English sentence on the capability.
//  · path   — the exact click-trail to reach it (rendered as a breadcrumb).
//  · where  — extra locating detail (which control, when it appears).
//  · helps  — the concrete benefit to the administrator.
//  · example— a realistic input → output so the value is obvious at a glance.
const AI_FEATURE_LABELS: Array<{
  key: AiFeatureName;
  label: string;
  what: string;
  path: string[];
  where: string;
  helps: string;
  example: string;
}> = [
  {
    key: "policyAuthoring",
    label: "Policy authoring",
    what: "Turns a plain-English description into structured policy conditions in the visual condition builder.",
    path: [
      "Any application",
      "Policies",
      "New policy (or open a policy → Edit)",
      "“Draft policy with AI” box",
    ],
    where:
      "Inside the condition builder of the New Policy dialog and the policy editor. Type a sentence and the matching conditions appear in the visual builder for you to review before saving.",
    helps:
      "Authors express intent in natural language instead of hand-crafting attribute expressions — fewer mistakes and faster authoring.",
    example:
      "You type “Allow when the amount is under 5000 and the region is EU” → it builds the conditions amount < 5000 AND region = EU.",
  },
  {
    key: "decisionExplainer",
    label: "Decision explainer",
    what: "Explains in plain English why a simulated request was allowed or denied, and suggests how to fix a denial.",
    path: [
      "Any application",
      "Simulator",
      "Run a simulation",
      "“Explain this decision” button on the result",
    ],
    where:
      "In the result card that appears after you run the simulator. The button sits next to the ALLOW/DENY outcome.",
    helps:
      "Makes authorization outcomes understandable without reading raw policy logic — and tells the admin exactly what to change.",
    example:
      "For a DENY it reports “Denied because priceStatus was not ‘ready to publish’” and lists the steps to make it pass.",
  },
  {
    key: "impactAnalysis",
    label: "Impact analysis",
    what: "Estimates the blast radius of publishing a draft policy — how many existing decisions would flip.",
    path: [
      "Any application",
      "Policies",
      "Open a DRAFT policy",
      "“Analyze publish impact” button",
    ],
    where:
      "On the policy detail page, but only for DRAFT policies (published policies have already taken effect). Advisory only — it never blocks publishing.",
    helps:
      "Surfaces unintended consequences before a policy goes live, so admins catch over-broad rules early.",
    example:
      "Before publishing a DENY it reports “Changes 3 of 42 evaluated requests — 3 newly denied, 0 newly allowed.”",
  },
  {
    key: "configAdvisor",
    label: "Config advisor",
    what: "Summarizes an application’s configuration health and suggests fixes for the findings it detects.",
    path: [
      "Any application",
      "Dashboard",
      "“Summarize with AI” button",
    ],
    where:
      "At the top of the application Dashboard. It reads the app’s roles, permissions and policies and writes an overview plus a fix per finding.",
    helps:
      "Gives admins a quick read on configuration risks and concrete next steps without hunting through every screen.",
    example:
      "It writes “2 roles grant no permissions and 1 policy is still a draft” with a suggested fix for each.",
  },
  {
    key: "accessSearch",
    label: "Access search",
    what: "Answers natural-language questions about who has access to what, across the platform or one app.",
    path: [
      "Platform → Access lens (or an application’s access view)",
      "“Ask AI” next to the search box",
    ],
    where:
      "The search bar at the top of the Access lens page, and the equivalent per-application access views. Example question chips are shown to get you started.",
    helps:
      "Replaces manual access-matrix spelunking with a direct question — answers in seconds instead of cross-referencing tables.",
    example:
      "You ask “Who can publish prices?” → it returns the matching subjects with a plain-English explanation of why.",
  },
  {
    key: "sodAnalysis",
    label: "Separation of duties",
    what: "Drafts a separation-of-duties rule from a plain-English description of the conflict you want to prevent.",
    path: [
      "Any application",
      "Dashboard",
      "“Draft rule with AI” button",
    ],
    where:
      "On the application Dashboard, in the separation-of-duties area. The drafted rule is shown for you to review and save — nothing is created until you confirm.",
    helps:
      "Speeds up authoring SoD rules by translating intent into a structured conflict definition (the two permissions and a severity).",
    example:
      "You type “Nobody who can submit a price may also publish it” → it drafts a submit-vs-publish conflict rule at HIGH severity.",
  },
  {
    key: "accessCertification",
    label: "Access certification",
    what: "Reviews a user’s grants and recommends keep / revoke / review for each, with a risk rationale.",
    path: [
      "Platform",
      "Users",
      "Open a user",
      "“Review access” button",
    ],
    where:
      "On the user detail page, in the action bar above their grants. The recommendations open in a side panel next to the grant list.",
    helps:
      "Focuses access recertification on the riskiest and most dormant grants, so reviewers spend time where it matters.",
    example:
      "It flags a never-used “Pricing Admin” grant as HIGH risk and recommends revoke, while marking active low-risk grants keep.",
  },
  {
    key: "auditNarrative",
    label: "Audit narrative",
    what: "Generates a plain-English narrative summary of a set of audit events for reviews and reporting.",
    path: [
      "Platform",
      "Audit",
      "“Generate summary” button",
    ],
    where:
      "In the header of the Platform Audit page. The summary opens in a side panel and groups related events, citing the concrete records it describes.",
    helps:
      "Turns raw event logs into a readable account — no manual scrolling to understand what happened in a period.",
    example:
      "It writes “16 events over 30 days; most denials were missing-context on price.publish” with the events grouped per application.",
  },
];

/** Thin wrapper over the shared icon set for the KPI info-boxes. */
function StatIcon({ name }: { name: string }) {
  return <AppIcon name={name} size={22} />;
}

export function PlatformOverviewPage() {
  const overview = usePlatformOverview();
  const users = useUsersDirectory();
  const tenants = useTenants();
  const applications = useApplications();
  const navigate = useNavigate();
  const chart = useChartTheme();
  const ai = useAiConfig();
  const [mapOpen, setMapOpen] = useState(false);
  const mapRows = useMemo<TenantAppRow[]>(() => {
    const apps = applications.data ?? [];
    return (tenants.data ?? []).map((t) => ({
      tenantId: t.tenantId,
      name: t.name,
      status: t.status,
      apps: apps.filter((a) => a.tenantId === t.tenantId),
    }));
  }, [tenants.data, applications.data]);
  if (overview.isLoading) return <Spinner label="Loading platform overview…" />;
  if (overview.isError || !overview.data)
    return (
      <EmptyBlock title="Overview unavailable" hint="Try again shortly." />
    );
  const o = overview.data;

  const stats = [
    {
      label: "Tenants",
      value: o.tenantCount,
      to: platformPaths.tenants,
      icon: "tenants",
      tone: "accent",
    },
    {
      label: "Applications",
      value: o.applicationCount,
      to: platformPaths.applications,
      icon: "applications",
      tone: "accent",
    },
    {
      label: "Users",
      value: users.data?.length,
      to: platformPaths.users,
      icon: "users",
      tone: "info",
    },
    { label: "Roles", value: o.roleCount, icon: "roles", tone: "accent" },
    {
      label: "Permissions",
      value: o.permissionCount,
      icon: "permissions",
      tone: "accent",
    },
    {
      label: "Policies",
      value: o.policyCount,
      icon: "policies",
      tone: "accent",
    },
    {
      label: "Assignments",
      value: o.assignmentCount,
      icon: "assignments",
      tone: "warning",
    },
    {
      label: "Active assignments",
      value: o.activeAssignmentCount,
      icon: "active",
      tone: "success",
    },
  ];

  const riskData = RISK_ORDER.map((risk) => ({
    label: risk.charAt(0) + risk.slice(1).toLowerCase(),
    value: o.applicationsByRisk[risk] ?? 0,
    color: chart.risk[risk as keyof typeof chart.risk],
  }));
  const tenantData = o.applicationsByTenant.map((t) => ({
    label: t.tenantName,
    value: t.applicationCount,
  }));
  const activityTrend = buildActivityTrend(
    o.recentAudit,
    getRuntimeConfig().ui.activityTrendDays,
  );

  return (
    <section className="page">
      <Breadcrumbs items={[{ label: "Platform" }, { label: "Overview" }]} />
      <header className="page-head">
        <div className="page-head-titles">
          <h1>Platform overview</h1>
          <p className="page-sub">
            Global administration across every tenant and application.
          </p>
        </div>
        <div className="page-head-actions">
          <button
            type="button"
            className="btn-secondary"
            disabled={mapRows.length === 0}
            onClick={() => setMapOpen(true)}
          >
            Platform map
          </button>
        </div>
      </header>

      <div className="stat-grid">
        {stats.map((s) => {
          const body = (
            <>
              <span className={`stat-icon tone-${s.tone}`} aria-hidden="true">
                <StatIcon name={s.icon} />
              </span>
              <span className="stat-body">
                <span className="stat-value">{s.value ?? "—"}</span>
                <span className="stat-label">{s.label}</span>
              </span>
            </>
          );
          return s.to ? (
            <Link key={s.label} className="stat-card stat-link" to={s.to}>
              {body}
            </Link>
          ) : (
            <div key={s.label} className="stat-card">
              {body}
            </div>
          );
        })}
      </div>

      <section className="panel">
        <div className="panel-head-row">
          <h2>Activity trend</h2>
          <span className="muted">Last 14 days</span>
        </div>
        <AreaTrend data={activityTrend} height={190} />
      </section>

      <div className="panel-columns">
        <section className="panel">
          <h2>Applications by risk</h2>
          <BarDistribution data={riskData} height={210} />
        </section>

        <section className="panel">
          <h2>Applications by tenant</h2>
          {tenantData.length === 0 ? (
            <p className="explorer-none">No applications yet.</p>
          ) : (
            <BarDistribution
              data={tenantData}
              height={210}
              fallbackColor={chart.accent}
            />
          )}
        </section>
      </div>

      <section className="panel">
        <h2>Recent activity</h2>
        {o.recentAudit.length === 0 ? (
          <p className="explorer-none">No recent activity.</p>
        ) : (
          <ul className="event-feed">
            {o.recentAudit.map((e) => {
              const meta = describeEvent(e.eventType);
              return (
                <li key={e.eventId}>
                  <span
                    className={`activity-icon tone-${meta.tone}`}
                    aria-hidden="true"
                  >
                    {meta.icon}
                  </span>
                  <div className="event-body">
                    <span className="event-title">{meta.label}</span>
                    <span className="event-meta">
                      {e.applicationId || "platform"} ·{" "}
                      {relativeTime(e.timestamp)}
                    </span>
                  </div>
                  <span className="event-cat">{meta.category}</span>
                </li>
              );
            })}
          </ul>
        )}
      </section>

      <section className="panel">
        <div className="panel-head-row">
          <h2>AI assistance</h2>
          <StatusChip value={ai.enabled ? "Enabled" : "Disabled"} />
        </div>
        {ai.enabled ? (
          <>
            <div className="ai-cap-meta">
              <span className="ai-cap-meta-item">
                <span className="muted">Provider</span>
                <strong>{ai.provider ?? "—"}</strong>
              </span>
              {ai.model && (
                <span className="ai-cap-meta-item">
                  <span className="muted">Model</span>
                  <strong>{ai.model}</strong>
                </span>
              )}
              {ai.limits && (
                <>
                  <span className="ai-cap-meta-item">
                    <span className="muted">Request timeout</span>
                    <strong>{ai.limits.requestTimeoutSeconds}s</strong>
                  </span>
                  <span className="ai-cap-meta-item">
                    <span className="muted">Max tokens</span>
                    <strong>{ai.limits.maxTokens}</strong>
                  </span>
                  <span className="ai-cap-meta-item">
                    <span className="muted">Temperature</span>
                    <strong>{ai.limits.temperature}</strong>
                  </span>
                </>
              )}
            </div>
            <ul className="ai-cap-grid">
              {AI_FEATURE_LABELS.map((f) => {
                const on = ai.features[f.key];
                const temp = ai.featureTemperatures?.[f.key];
                return (
                  <li key={f.key} className="ai-cap-row">
                    <span className="ai-cap-label">{f.label}</span>
                    <span className="ai-cap-status">
                      {on && temp != null && (
                        <span className="muted">Temperature {temp}</span>
                      )}
                      <span
                        className={`status-chip ${on ? "enabled" : "disabled"}`}
                      >
                        {on ? "On" : "Off"}
                      </span>
                    </span>
                  </li>
                );
              })}
            </ul>
          </>
        ) : (
          <p className="explorer-none">
            AI assistance is turned off. Configure a provider on the server to
            enable the advisory features.
          </p>
        )}
      </section>

      <VizModal
        title="Platform map"
        subtitle="Tenant → Application"
        open={mapOpen}
        onClose={() => setMapOpen(false)}
        legend={
          <>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-tenant" /> Tenant
            </span>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-app" /> Application
            </span>
          </>
        }
      >
        <D3Tree
          data={buildTenantAppTree("All tenants", mapRows)}
          height={640}
          onSelect={(node) => {
            const key = node.id.slice(node.id.indexOf(":") + 1);
            if (node.kind === "application") {
              navigate(appPaths.dashboard(key));
              setMapOpen(false);
            } else if (node.kind === "tenant") {
              navigate(platformPaths.tenant(key));
              setMapOpen(false);
            }
          }}
          ariaLabel="Platform tenant and application map"
        />
      </VizModal>
    </section>
  );
}

export function ApplicationsPage() {
  const navigate = useNavigate();
  const caps = useCapabilities();
  const tenants = useTenants();
  const [q, setQ] = useUrlState("q");
  const [status, setStatus] = useUrlState("status");
  const [riskLevel, setRiskLevel] = useUrlState("risk");
  const [tenantId, setTenantId] = useUrlState("tenant");
  const [creating, setCreating] = useState(false);
  const [mapOpen, setMapOpen] = useState(false);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = usePageSizeState();

  const filters = useMemo(
    () => ({
      q: q || undefined,
      status: status || undefined,
      riskLevel: riskLevel || undefined,
      tenantId: tenantId || undefined,
    }),
    [q, status, riskLevel, tenantId],
  );
  // Reset to the first page whenever the query/filters change.
  useEffect(() => {
    setPage(1);
  }, [q, status, riskLevel, tenantId, pageSize]);
  const apps = useFilteredApplicationsPaged({ ...filters, page, pageSize });
  // The portfolio map needs the full filtered set, not a single page — fetch it
  // lazily only while the modal is open.
  const mapApps = useFilteredApplications(filters, mapOpen);

  const mapRows = useMemo<TenantAppRow[]>(() => {
    const list = mapApps.data ?? [];
    const rows = (tenants.data ?? []).map((t) => ({
      tenantId: t.tenantId,
      name: t.name,
      status: t.status,
      apps: list.filter((a) => a.tenantId === t.tenantId),
    }));
    const orphans = list.filter(
      (a) =>
        !a.tenantId || !tenants.data?.some((t) => t.tenantId === a.tenantId),
    );
    if (orphans.length > 0) {
      rows.push({
        tenantId: "__none__",
        name: "Unassigned",
        status: "—",
        apps: orphans,
      });
    }
    return rows.filter((r) => r.apps.length > 0);
  }, [mapApps.data, tenants.data]);

  const tenantName = (id: string | null | undefined) =>
    (id && tenants.data?.find((t) => t.tenantId === id)?.name) || "Unassigned";

  const columns: DataTableColumn<ApplicationSummary>[] = [
    {
      key: "name",
      header: "Application",
      sortValue: (a) => a.name,
      searchValue: (a) => `${a.name} ${a.applicationId}`,
      render: (a) => (
        <button
          type="button"
          className="link-cell"
          onClick={() => navigate(appPaths.dashboard(a.applicationId))}
        >
          {a.name}
        </button>
      ),
    },
    {
      key: "tenant",
      header: "Tenant",
      sortValue: (a) => tenantName(a.tenantId),
      render: (a) => tenantName(a.tenantId),
    },
    {
      key: "risk",
      header: "Risk",
      sortValue: (a) => a.riskLevel,
      render: (a) => (
        <>
          <RiskDot level={a.riskLevel} /> {a.riskLevel}
        </>
      ),
    },
    {
      key: "status",
      header: "Status",
      sortValue: (a) => a.status,
      render: (a) => <StatusChip value={a.status} />,
    },
  ];

  return (
    <section className="page">
      <Breadcrumbs items={[{ label: "Platform" }, { label: "Applications" }]} />
      <header className="page-head">
        <div className="page-head-titles">
          <h1>Applications</h1>
          <p className="page-sub">
            Every application registered across all tenants.
          </p>
        </div>
        {caps.canManagePlatform && (
          <div className="page-head-actions">
            <button
              type="button"
              className="btn-secondary"
              disabled={(apps.data?.total ?? 0) === 0}
              onClick={() => setMapOpen(true)}
            >
              Portfolio map
            </button>
            <button
              type="button"
              className="btn-primary"
              onClick={() => setCreating(true)}
            >
              New application
            </button>
          </div>
        )}
        {!caps.canManagePlatform && (
          <div className="page-head-actions">
            <button
              type="button"
              className="btn-secondary"
              disabled={(apps.data?.total ?? 0) === 0}
              onClick={() => setMapOpen(true)}
            >
              Portfolio map
            </button>
          </div>
        )}
      </header>

      <DataTable
        columns={columns}
        rows={apps.data?.items ?? []}
        getRowKey={(a) => a.applicationId}
        isLoading={apps.isLoading}
        isError={apps.isError}
        emptyMessage="No applications match these filters."
        searchPlaceholder="Search applications…"
        searchValue={q}
        onSearchChange={setQ}
        serverPagination={{
          page,
          pageSize,
          total: apps.data?.total ?? 0,
          onPageChange: setPage,
          onPageSizeChange: setPageSize,
        }}
        filters={[
          {
            key: "tenant",
            label: "Tenant",
            value: tenantId,
            onChange: setTenantId,
            options: [
              { value: "", label: "All tenants" },
              ...(tenants.data ?? []).map((t) => ({
                value: t.tenantId,
                label: t.name,
              })),
            ],
          },
          {
            key: "status",
            label: "Status",
            value: status,
            onChange: setStatus,
            options: [
              { value: "", label: "All statuses" },
              { value: "ACTIVE", label: "Active" },
              { value: "DISABLED", label: "Disabled" },
              { value: "ARCHIVED", label: "Archived" },
            ],
          },
          {
            key: "risk",
            label: "Risk",
            value: riskLevel,
            onChange: setRiskLevel,
            options: [
              { value: "", label: "All risk" },
              ...RISK_ORDER.map((r) => ({ value: r, label: r })),
            ],
          },
        ]}
        onClearFilters={() => {
          setQ("");
          setTenantId("");
          setStatus("");
          setRiskLevel("");
        }}
      />

      {creating && (
        <ApplicationForm
          initialTenantId={tenantId || undefined}
          onClose={() => setCreating(false)}
        />
      )}

      <VizModal
        title="Portfolio map"
        subtitle="Tenant → Application"
        open={mapOpen}
        onClose={() => setMapOpen(false)}
        legend={
          <>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-tenant" /> Tenant
            </span>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-app" /> Application
            </span>
          </>
        }
      >
        <D3Tree
          data={buildTenantAppTree("Applications", mapRows)}
          height={640}
          onSelect={(node) => {
            if (node.kind === "application") {
              navigate(
                appPaths.dashboard(node.id.slice(node.id.indexOf(":") + 1)),
              );
              setMapOpen(false);
            }
          }}
          ariaLabel="Application portfolio map"
        />
      </VizModal>
    </section>
  );
}

export function TenantsPage() {
  const navigate = useNavigate();
  return (
    <section className="page">
      <Breadcrumbs items={[{ label: "Platform" }, { label: "Tenants" }]} />
      <TenantsPanel
        onSelectApp={(appId) => navigate(appPaths.dashboard(appId))}
        onSelectTenant={(tid) => navigate(platformPaths.tenant(tid))}
      />
    </section>
  );
}

export function TenantDetailPage() {
  const { tenantId = "" } = useParams();
  const navigate = useNavigate();
  const detail = useTenantDetail(tenantId);
  const [status, setStatus] = useState("");
  const [risk, setRisk] = useState("");
  const [graphOpen, setGraphOpen] = useState(false);
  if (detail.isLoading) return <Spinner label="Loading tenant…" />;
  if (detail.isError || !detail.data)
    return (
      <EmptyBlock title="Tenant not found" hint="It may have been removed." />
    );
  const d = detail.data;
  const tenantTree = buildTenantSubtree(
    tenantId,
    d.tenant.name,
    d.applications,
  );

  const columns: DataTableColumn<TenantApplicationSummary>[] = [
    {
      key: "name",
      header: "Application",
      sortValue: (a) => a.name,
      render: (a) => (
        <button
          type="button"
          className="link-cell"
          onClick={() => navigate(appPaths.dashboard(a.applicationId))}
        >
          {a.name}
        </button>
      ),
    },
    {
      key: "risk",
      header: "Risk",
      sortValue: (a) => a.riskLevel,
      render: (a) => (
        <>
          <RiskDot level={a.riskLevel} /> {a.riskLevel}
        </>
      ),
    },
    {
      key: "status",
      header: "Status",
      sortValue: (a) => a.status,
      render: (a) => <StatusChip value={a.status} />,
    },
    {
      key: "roles",
      header: "Roles",
      sortValue: (a) => a.roleCount,
      render: (a) => a.roleCount,
    },
    {
      key: "assignments",
      header: "Assignments",
      sortValue: (a) => a.assignmentCount,
      render: (a) => `${a.activeAssignmentCount} / ${a.assignmentCount}`,
    },
  ];

  return (
    <section className="page">
      <Breadcrumbs
        items={[
          { label: "Platform" },
          { label: "Tenants", to: platformPaths.tenants },
          { label: d.tenant.name },
        ]}
      />
      <header className="page-head">
        <div className="page-head-titles">
          <h1>{d.tenant.name}</h1>
          {d.tenant.description && (
            <p className="page-sub">{d.tenant.description}</p>
          )}
        </div>
        <StatusChip value={d.tenant.status} />
      </header>

      <div className="stat-grid">
        <div className="stat-card">
          <span className="stat-icon tone-accent" aria-hidden="true">
            <StatIcon name="applications" />
          </span>
          <span className="stat-body">
            <span className="stat-value">{d.rollup.applicationCount}</span>
            <span className="stat-label">Applications</span>
          </span>
        </div>
        <div className="stat-card">
          <span className="stat-icon tone-accent" aria-hidden="true">
            <StatIcon name="roles" />
          </span>
          <span className="stat-body">
            <span className="stat-value">{d.rollup.roleCount}</span>
            <span className="stat-label">Roles</span>
          </span>
        </div>
        <div className="stat-card">
          <span className="stat-icon tone-warning" aria-hidden="true">
            <StatIcon name="assignments" />
          </span>
          <span className="stat-body">
            <span className="stat-value">{d.rollup.assignmentCount}</span>
            <span className="stat-label">Assignments</span>
          </span>
        </div>
        <div className="stat-card">
          <span className="stat-icon tone-success" aria-hidden="true">
            <StatIcon name="active" />
          </span>
          <span className="stat-body">
            <span className="stat-value">{d.rollup.activeAssignmentCount}</span>
            <span className="stat-label">Active</span>
          </span>
        </div>
      </div>

      <DataTable
        columns={columns}
        rows={d.applications.filter(
          (a) =>
            (!status || a.status === status) && (!risk || a.riskLevel === risk),
        )}
        getRowKey={(a) => a.applicationId}
        emptyMessage="No applications owned by this tenant."
        searchPlaceholder="Search applications…"
        filters={[
          {
            key: "status",
            label: "Status",
            value: status,
            onChange: setStatus,
            options: [
              { value: "", label: "All statuses" },
              ...Array.from(new Set(d.applications.map((a) => a.status)))
                .sort()
                .map((s) => ({ value: s, label: s })),
            ],
          },
          {
            key: "risk",
            label: "Risk",
            value: risk,
            onChange: setRisk,
            options: [
              { value: "", label: "All risk" },
              ...RISK_ORDER.map((r) => ({ value: r, label: r })),
            ],
          },
        ]}
        onClearFilters={() => {
          setStatus("");
          setRisk("");
        }}
      />

      {d.applications.length > 0 && (
        <section className="panel">
          <div className="panel-head-row">
            <h2>Tenant map</h2>
            <button
              type="button"
              className="btn-secondary btn-sm"
              onClick={() => setGraphOpen(true)}
            >
              Expand
            </button>
          </div>
          <div className="tenant-graph">
            <D3Tree
              data={tenantTree}
              height={260}
              onSelect={(node) => {
                if (node.kind === "application") {
                  navigate(
                    appPaths.dashboard(node.id.slice(node.id.indexOf(":") + 1)),
                  );
                }
              }}
              ariaLabel={`${d.tenant.name} application map`}
            />
          </div>
        </section>
      )}

      <VizModal
        title={`${d.tenant.name} — application map`}
        subtitle="Tenant → Application"
        open={graphOpen}
        onClose={() => setGraphOpen(false)}
        legend={
          <>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-tenant" /> Tenant
            </span>
            <span className="viz-legend-item">
              <span className="viz-legend-swatch seg-app" /> Application
            </span>
          </>
        }
      >
        <D3Tree
          data={tenantTree}
          height={640}
          onSelect={(node) => {
            if (node.kind === "application") {
              navigate(
                appPaths.dashboard(node.id.slice(node.id.indexOf(":") + 1)),
              );
              setGraphOpen(false);
            }
          }}
          ariaLabel={`${d.tenant.name} application map`}
        />
      </VizModal>
    </section>
  );
}

export function UsersPage() {
  const navigate = useNavigate();
  return (
    <section className="page">
      <Breadcrumbs items={[{ label: "Platform" }, { label: "Users" }]} />
      <UsersPanel
        onSelectUser={(email) => navigate(platformPaths.user(email))}
      />
    </section>
  );
}

export function UserDetailPage() {
  const { email = "" } = useParams();
  const navigate = useNavigate();
  return (
    <section className="page">
      <Breadcrumbs
        items={[
          { label: "Platform" },
          { label: "Users", to: platformPaths.users },
          { label: email },
        ]}
      />
      <UserDetails
        email={email}
        onSelectApp={(appId) => navigate(appPaths.dashboard(appId))}
      />
    </section>
  );
}

export function AccessLensPage() {
  const navigate = useNavigate();
  return (
    <section className="page">
      <Breadcrumbs items={[{ label: "Platform" }, { label: "Access lens" }]} />
      <HierarchyExplorer
        onSelectApp={(appId) => navigate(appPaths.dashboard(appId))}
      />
    </section>
  );
}

export function AskAiPage() {
  const enabled = useAiFeature("accessSearch");
  return (
    <section className="page">
      <Breadcrumbs items={[{ label: "Platform" }, { label: "Ask AI" }]} />
      <header className="page-head">
        <div className="page-head-titles">
          <h1>Ask AI</h1>
          <p className="page-sub">
            Ask natural-language questions about access across every application
            and get auditable, citation-backed answers in seconds.
          </p>
        </div>
      </header>
      {enabled ? (
        <AccessSearch />
      ) : (
        <EmptyBlock
          title="Ask AI is turned off"
          hint="Enable AI and the access search feature on the server to ask questions."
        />
      )}
    </section>
  );
}

export function AuditPage() {
  const [q, setQ] = useUrlState("q");
  const [category, setCategory] = useUrlState("category");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = usePageSizeState();
  const [expanded, setExpanded] = useState<string | null>(null);
  useEffect(() => setPage(1), [q, category, pageSize]);

  const feed = useAuditFeed({
    q: q || undefined,
    category: category || undefined,
    page,
    pageSize,
  });
  const summary = useAuditActivitySummary();
  const events = feed.data?.items ?? [];
  const total = feed.data?.total ?? 0;
  const hasAny = (summary.data?.total ?? 0) > 0;
  const groups = useMemo(() => {
    const map = new Map<string, typeof events>();
    for (const e of events) {
      const bucket = dayBucket(e.timestamp);
      const list = map.get(bucket) ?? [];
      list.push(e);
      map.set(bucket, list);
    }
    return Array.from(map.entries());
  }, [events]);
  const narrativeEnabled = useAiFeature("auditNarrative");
  const narrative = useAuditNarrative();
  const [narrativeOpen, setNarrativeOpen] = useState(false);
  const runNarrative = () => {
    setNarrativeOpen(true);
    narrative.mutate({});
  };
  return (
    <section className="page">
      <Breadcrumbs items={[{ label: "Platform" }, { label: "Audit" }]} />
      <header className="page-head">
        <div className="page-head-titles">
          <h1>Audit</h1>
          <p className="page-sub">
            Recent governance events across all accessible applications.
          </p>
        </div>
        {narrativeEnabled && hasAny && (
          <div className="page-head-actions">
            <button
              type="button"
              className="btn-secondary btn-sm"
              onClick={runNarrative}
              disabled={narrative.isPending}
            >
              {narrative.isPending ? "Summarizing…" : "Generate summary"}
            </button>
          </div>
        )}
      </header>
      {feed.isLoading && !feed.data ? (
        <Spinner label="Loading audit events…" />
      ) : !hasAny ? (
        <EmptyBlock
          title="No audit events"
          hint="Governance activity will appear here."
        />
      ) : (
        <>
          <section className="panel">
            <div className="panel-head-row">
              <h2>Activity calendar</h2>
            </div>
            <div className="calendar-3q">
              <D3Heatmap
                dayCounts={summary.data?.days}
                weeks={16}
                ariaLabel="Audit activity by day"
              />
            </div>
          </section>
          <FilterBar
            search={{
              value: q,
              onChange: setQ,
              placeholder: "Search events, app or actor…",
              label: "Search audit events",
            }}
            filters={[
              {
                key: "category",
                label: "Category",
                value: category,
                onChange: setCategory,
                options: [
                  { value: "", label: "All categories" },
                  ...AUDIT_CATEGORIES.map((c) => ({ value: c, label: c })),
                ],
              },
            ]}
            onClearAll={() => {
              setQ("");
              setCategory("");
            }}
          />
          {events.length === 0 ? (
            <EmptyBlock
              title="No matching events"
              hint="Try a different search or category."
            />
          ) : (
            <div className="activity-feed">
              {groups.map(([bucket, list]) => (
                <section key={bucket} className="activity-group">
                  <h2 className="activity-group-head">
                    {bucket}
                    <span className="muted">{list.length}</span>
                  </h2>
                  <ol className="activity-list">
                    {list.map((e, i) => {
                      const id = e.eventId ?? `${bucket}-${i}`;
                      return (
                        <AuditEventCard
                          key={id}
                          event={e}
                          scope="platform"
                          id={id}
                          isOpen={expanded === id}
                          onToggle={(x) =>
                            setExpanded(expanded === x ? null : x)
                          }
                        />
                      );
                    })}
                  </ol>
                </section>
              ))}
            </div>
          )}
          {total > 0 && (
            <Pagination
              page={page}
              pageSize={pageSize}
              total={total}
              onPageChange={setPage}
              onPageSizeChange={setPageSize}
            />
          )}
        </>
      )}

      <DrawerPanel
        title="Audit summary"
        open={narrativeOpen}
        onClose={() => setNarrativeOpen(false)}
      >
        {narrative.isPending ? (
          <Spinner label="Summarizing audit activity…" />
        ) : narrative.isError ? (
          <EmptyBlock
            title="Summary failed"
            hint="The audit summary could not be generated. Please try again."
          />
        ) : narrative.data && narrative.data.totalEvents > 0 ? (
          <AuditNarrativeContent data={narrative.data} />
        ) : (
          <EmptyBlock
            title="No activity to summarize"
            hint="No audit events were recorded in the selected window."
          />
        )}
      </DrawerPanel>
    </section>
  );
}

/** Renders the AI audit narrative: a plain-language summary plus grounded
 * sections, each citing the concrete events it describes. Real identities are
 * shown to the (authorized) auditor; the model only ever saw pseudonyms. */
function AuditNarrativeContent({ data }: { data: AuditNarrativeResponse }) {
  const eventsById = useMemo(() => {
    const map = new Map<string, AuditNarrativeEvent>();
    for (const e of data.events) map.set(e.eventId, e);
    return map;
  }, [data.events]);
  const window = `${formatDate(data.fromUtc)} – ${formatDate(data.toUtc)}`;
  return (
    <div>
      <p className="muted" style={{ marginTop: 0 }}>
        {data.totalEvents} event{data.totalEvents === 1 ? "" : "s"} · {window}
      </p>
      {data.summary && <p className="advisor-summary">{data.summary}</p>}
      {data.sections.length > 0 && (
        <ul className="advisor-list">
          {data.sections.map((section, idx) => (
            <li key={idx} className="advisor-item">
              <span className="advisor-body">
                <span className="advisor-title">
                  {section.heading}
                  <span className="muted">
                    {" "}
                    · {section.eventIds.length} event
                    {section.eventIds.length === 1 ? "" : "s"}
                  </span>
                </span>
                <span className="advisor-detail muted">{section.detail}</span>
                {section.eventIds.length > 0 && (
                  <span className="sod-perm-chips">
                    {section.eventIds.map((id) => {
                      const e = eventsById.get(id);
                      if (!e) return null;
                      return (
                        <span key={id} className="sod-chip">
                          {describeEvent(e.eventType).label} ·{" "}
                          {e.applicationId || "platform"} ·{" "}
                          {relativeTime(e.timestamp)}
                        </span>
                      );
                    })}
                  </span>
                )}
              </span>
            </li>
          ))}
        </ul>
      )}
      {data.topDenyReasons.length > 0 && (
        <>
          <h3 className="drawer-subhead">Top denial reasons</h3>
          <ul className="advisor-list">
            {data.topDenyReasons.map((deny, idx) => (
              <li key={idx} className="advisor-item">
                <span className="advisor-body">
                  <span className="advisor-title">
                    {deny.denyReason}
                    <span className="muted"> · {deny.count}×</span>
                  </span>
                  <span className="advisor-detail muted">
                    {deny.applicationId}
                  </span>
                </span>
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  );
}

// Friendly labels reused for the AI usage report (feature keys ↔ display names).
const AI_FEATURE_LABEL_MAP: Record<string, string> = Object.fromEntries(
  AI_FEATURE_LABELS.map((f) => [f.key, f.label]),
);

function aiFeatureLabel(feature: string): string {
  return AI_FEATURE_LABEL_MAP[feature] ?? feature;
}

function formatTokens(value: number): string {
  return value.toLocaleString();
}

function formatCost(value: number | null): string {
  if (value === null) return "—";
  if (value > 0 && value < 0.01) return "< $0.01";
  return `$${value.toFixed(2)}`;
}

/** Platform AI usage observability (Tier 1). Reads the metadata-only invocation
 * log: volume, outcome mix, latency, tokens and estimated cost, plus per-feature
 * and per-actor breakdowns. No prompts or responses are ever stored or shown. */
export function AiUsagePage() {
  const ai = useAiConfig();
  const [windowDays, setWindowDays] = useState(30);
  const usage = useAiUsage(windowDays);
  const report = usage.data?.report;

  const trendData = useMemo(
    () => buildUsageTrend(report?.trend ?? [], usage.data?.windowDays ?? windowDays),
    [report?.trend, usage.data?.windowDays, windowDays],
  );
  const featureBars = useMemo(
    () =>
      (report?.byFeature ?? []).map((f) => ({
        label: aiFeatureLabel(f.feature),
        value: f.count,
      })),
    [report?.byFeature],
  );

  const featureColumns: DataTableColumn<AiUsageFeatureCount>[] = [
    {
      key: "feature",
      header: "Feature",
      sortValue: (f) => aiFeatureLabel(f.feature),
      render: (f) => aiFeatureLabel(f.feature),
    },
    {
      key: "count",
      header: "Invocations",
      sortValue: (f) => f.count,
      render: (f) => f.count,
    },
    {
      key: "success",
      header: "Succeeded",
      sortValue: (f) => f.successCount,
      render: (f) => `${f.successCount} / ${f.count}`,
    },
    {
      key: "tokens",
      header: "Tokens",
      sortValue: (f) => f.totalTokens,
      render: (f) => formatTokens(f.totalTokens),
    },
  ];

  const actorColumns: DataTableColumn<AiUsageActorCount>[] = [
    {
      key: "actor",
      header: "Administrator",
      sortValue: (a) => a.actor,
      render: (a) => a.actor,
    },
    {
      key: "count",
      header: "Invocations",
      sortValue: (a) => a.count,
      render: (a) => a.count,
    },
  ];

  const successRate =
    report && report.totalInvocations > 0
      ? Math.round((report.successCount / report.totalInvocations) * 100)
      : 0;

  return (
    <section className="page">
      <Breadcrumbs items={[{ label: "Platform" }, { label: "AI usage" }]} />
      <header className="page-head">
        <div className="page-head-titles">
          <h1>AI usage</h1>
          <p className="page-sub">
            How platform administrators are using AI assistance. Aggregate charts
            use invocation metadata only; the prompt log below keeps the exact
            free-text prompts for review.
          </p>
        </div>
        <div className="page-head-actions">
          <div className="segmented" role="group" aria-label="Time window">
            {getRuntimeConfig().ui.aiReportingWindows.map((d) => (
              <button
                key={d}
                type="button"
                className={`segmented-item ${d === windowDays ? "active" : ""}`}
                onClick={() => setWindowDays(d)}
                aria-pressed={d === windowDays}
              >
                {d}d
              </button>
            ))}
          </div>
        </div>
      </header>

      <section className="panel">
        <div className="panel-head-row">
          <h2>Feature availability</h2>
          <span className="muted">
            {ai.enabled
              ? "Which AI features are enabled on the server."
              : "AI assistance is off, so every feature is disabled."}
          </span>
        </div>
        <ul className="ai-cap-grid">
          {AI_FEATURE_LABELS.map((f) => {
            // A feature is only "on" when the master switch is enabled AND the
            // feature flag is set; with AI off, every row shows Off.
            const on = ai.enabled && ai.features[f.key];
            return (
              <li key={f.key} className="ai-cap-row">
                <span className="ai-cap-label">{f.label}</span>
                <span className={`status-chip ${on ? "enabled" : "disabled"}`}>
                  {on ? "On" : "Off"}
                </span>
              </li>
            );
          })}
        </ul>
      </section>

      {!ai.enabled ? (
        <EmptyBlock
          title="AI assistance is turned off"
          hint="Configure an AI provider on the server to start collecting usage."
        />
      ) : usage.isLoading ? (
        <Spinner label="Loading AI usage…" />
      ) : usage.isError ? (
        <EmptyBlock
          title="Could not load AI usage"
          hint="Please try again in a moment."
        />
      ) : !report || report.totalInvocations === 0 ? (
        <EmptyBlock
          title="No AI activity yet"
          hint="Usage will appear here once administrators use AI features."
        />
      ) : (
        <>
          <div className="stat-grid">
            <div className="stat-card">
              <span className="stat-icon tone-info" aria-hidden="true">
                <StatIcon name="audit" />
              </span>
              <span className="stat-body">
                <span className="stat-value">{report.totalInvocations}</span>
                <span className="stat-label">Invocations</span>
              </span>
            </div>
            <div className="stat-card">
              <span className="stat-icon tone-success" aria-hidden="true">
                <StatIcon name="active" />
              </span>
              <span className="stat-body">
                <span className="stat-value">{successRate}%</span>
                <span className="stat-label">Success rate</span>
              </span>
            </div>
            <div className="stat-card">
              <span className="stat-icon tone-accent" aria-hidden="true">
                <StatIcon name="settings" />
              </span>
              <span className="stat-body">
                <span className="stat-value">
                  {formatCost(report.estimatedCostUsd)}
                </span>
                <span className="stat-label">Estimated cost</span>
              </span>
            </div>
            <div className="stat-card">
              <span className="stat-icon tone-warning" aria-hidden="true">
                <StatIcon name="applications" />
              </span>
              <span className="stat-body">
                <span className="stat-value">
                  {formatTokens(report.totalTokens)}
                </span>
                <span className="stat-label">Total tokens</span>
              </span>
            </div>
          </div>

          <section className="panel">
            <div className="panel-head-row">
              <h2>Usage trend</h2>
              <span className="muted">Last {usage.data?.windowDays} days</span>
            </div>
            <AreaTrend data={trendData} height={190} />
          </section>

          <div className="panel-columns">
            <section className="panel">
              <h2>Invocations by feature</h2>
              <BarDistribution data={featureBars} height={210} />
            </section>

            <section className="panel">
              <h2>Outcomes &amp; latency</h2>
              <ul className="ai-cap-grid">
                <li className="ai-cap-row">
                  <span className="ai-cap-label">Succeeded</span>
                  <span className="status-chip enabled">
                    {report.successCount}
                  </span>
                </li>
                <li className="ai-cap-row">
                  <span className="ai-cap-label">Timed out</span>
                  <span className="status-chip disabled">
                    {report.timeoutCount}
                  </span>
                </li>
                <li className="ai-cap-row">
                  <span className="ai-cap-label">Errored</span>
                  <span className="status-chip disabled">
                    {report.errorCount}
                  </span>
                </li>
                <li className="ai-cap-row">
                  <span className="ai-cap-label">Latency p50</span>
                  <strong>{report.p50LatencyMs} ms</strong>
                </li>
                <li className="ai-cap-row">
                  <span className="ai-cap-label">Latency p95</span>
                  <strong>{report.p95LatencyMs} ms</strong>
                </li>
              </ul>
            </section>
          </div>

          <section className="panel">
            <h2>By feature</h2>
            <DataTable
              columns={featureColumns}
              rows={report.byFeature}
              getRowKey={(f) => f.feature}
              searchable={false}
              emptyMessage="No feature usage in this window."
            />
          </section>

          {report.byActor.length > 0 && (
            <section className="panel">
              <h2>By administrator</h2>
              <DataTable
                columns={actorColumns}
                rows={report.byActor}
                getRowKey={(a) => a.actor}
                searchable={false}
                emptyMessage="No administrator usage in this window."
              />
            </section>
          )}

          <AiPromptLogPanel windowDays={windowDays} />
        </>
      )}
    </section>
  );
}

const AI_PROMPTLOG_FEATURES = [
  { key: "", label: "All features" },
  { key: "accessSearch", label: "Access search" },
  { key: "sodAnalysis", label: "SoD rule draft" },
  { key: "policyAuthoring", label: "Policy authoring" },
];

function formatDateTime(iso: string): string {
  return new Date(iso).toLocaleString(undefined, {
    month: "short",
    day: "numeric",
    hour: "2-digit",
    minute: "2-digit",
  });
}

// Failed prompts (anything other than a clean success) are shown with the
// "disabled" (red) chip so they stand out for review.
function promptOutcomeTone(outcome: string): "enabled" | "disabled" {
  return outcome === "Succeeded" ? "enabled" : "disabled";
}

function prettyJson(value: string): string {
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

/** Free-text AI prompt log (F1/F7/F8). Unlike the metadata usage report this
 * shows the exact prompt an administrator submitted, the terminal outcome
 * (including validation failures), and how the model interpreted the request,
 * so admins can review failed prompts and improve the underlying logic. */
function AiPromptLogPanel({ windowDays }: { windowDays: number }) {
  const [feature, setFeature] = useState("");
  const [failuresOnly, setFailuresOnly] = useState(false);
  const [expandedId, setExpandedId] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = usePageSizeState();
  useEffect(() => setPage(1), [feature, failuresOnly, windowDays, pageSize]);
  const logs = useAiPromptLogs({
    windowDays,
    feature: feature || undefined,
    failuresOnly: failuresOnly || undefined,
    page,
    pageSize,
  });
  const items = logs.data?.items ?? [];
  const total = logs.data?.total ?? 0;

  return (
    <section className="panel">
      <div className="panel-head-row">
        <h2>Prompt log</h2>
        <span className="muted">
          Exact free-text prompts submitted to AI features, kept for review.
        </span>
      </div>
      <div className="promptlog-controls">
        <label className="promptlog-select">
          <span className="visually-hidden">Feature</span>
          <select
            value={feature}
            onChange={(e) => setFeature(e.target.value)}
            aria-label="Filter by feature"
          >
            {AI_PROMPTLOG_FEATURES.map((f) => (
              <option key={f.key} value={f.key}>
                {f.label}
              </option>
            ))}
          </select>
        </label>
        <label className="promptlog-toggle">
          <input
            type="checkbox"
            checked={failuresOnly}
            onChange={(e) => setFailuresOnly(e.target.checked)}
          />
          Failures only
        </label>
      </div>

      {logs.isLoading ? (
        <Spinner label="Loading prompt log…" />
      ) : logs.isError ? (
        <EmptyBlock
          title="Could not load the prompt log"
          hint="Please try again in a moment."
        />
      ) : items.length === 0 ? (
        <EmptyBlock
          title="No prompts in this window"
          hint="Free-text AI prompts will appear here once administrators use them."
        />
      ) : (
        <div className="promptlog-list">
          {items.map((item: AiPromptLogItem) => {
            const open = expandedId === item.id;
            return (
              <div className="promptlog-item" key={item.id}>
                <button
                  type="button"
                  className="promptlog-row"
                  aria-expanded={open}
                  onClick={() => setExpandedId(open ? null : item.id)}
                >
                  <span
                    className={`promptlog-chevron ${open ? "is-open" : ""}`}
                    aria-hidden="true"
                  >
                    <AppIcon name="chevron" size={16} />
                  </span>
                  <span className="promptlog-time">
                    {formatDateTime(item.timestamp)}
                  </span>
                  <span className="promptlog-feature">
                    {aiFeatureLabel(item.feature)}
                  </span>
                  <span className="promptlog-text" title={item.promptText}>
                    {item.promptText}
                  </span>
                  <span
                    className={`status-chip ${promptOutcomeTone(item.outcome)}`}
                  >
                    {item.outcome}
                  </span>
                </button>
                {open && (
                  <dl className="promptlog-detail">
                    <div>
                      <dt>Prompt</dt>
                      <dd className="promptlog-pre">{item.promptText}</dd>
                    </div>
                    <div>
                      <dt>Administrator</dt>
                      <dd>
                        {item.actorEmail ?? "—"}
                        {item.actorRole ? ` · ${item.actorRole}` : ""}
                      </dd>
                    </div>
                    <div>
                      <dt>Application</dt>
                      <dd>{item.applicationId ?? "Platform-wide"}</dd>
                    </div>
                    <div>
                      <dt>Provider</dt>
                      <dd>
                        {item.provider}
                        {item.model ? ` · ${item.model}` : ""}
                      </dd>
                    </div>
                    {item.errorMessage && (
                      <div>
                        <dt>Error</dt>
                        <dd className="promptlog-error">{item.errorMessage}</dd>
                      </div>
                    )}
                    {item.interpretation && (
                      <div>
                        <dt>Interpretation</dt>
                        <dd>
                          <pre className="promptlog-pre">
                            {prettyJson(item.interpretation)}
                          </pre>
                        </dd>
                      </div>
                    )}
                  </dl>
                )}
              </div>
            );
          })}
        </div>
      )}
      {total > 0 && (
        <Pagination
          page={page}
          pageSize={pageSize}
          total={total}
          onPageChange={setPage}
          onPageSizeChange={setPageSize}
        />
      )}
    </section>
  );
}

// ── Platform access model (reference) ───────────────────────────────────────
// These mirror the enforced authorization model: the realm platform roles and
// the delegated per-application capability map in capabilities.tsx / the backend
// DelegatedAdminAuthorizationService. Descriptions are documentation of the real
// system, not configurable data.

const PLATFORM_ROLE_INFO: {
  claim: string;
  label: string;
  desc: string;
  access: string;
}[] = [
  {
    claim: "PlatformSuperAdmin",
    label: "Platform Super Admin",
    desc: "Full administration across every tenant and application.",
    access: "All capabilities · every application & tenant",
  },
  {
    claim: "PlatformReadOnlyViewer",
    label: "Platform Read-only Viewer",
    desc: "Read-only visibility into audit trails and configuration.",
    access: "View audit + read-only · every application",
  },
];

const CAPABILITY_LABEL: Record<Capability, string> = {
  ManageApplication: "Manage application",
  ManageRoles: "Manage roles",
  ManagePermissions: "Manage permissions",
  MapRolePermission: "Map role ↔ permission",
  ManagePolicies: "Manage policies",
  AssignRoles: "Assign roles",
  ViewAudit: "View audit",
  ReadOnlyView: "Read-only view",
};

const CAPABILITY_DESC: Record<Capability, string> = {
  ManageApplication:
    "Configure the application, its identity providers, and lifecycle.",
  ManageRoles: "Create, edit, and delete roles.",
  ManagePermissions: "Create, edit, and delete permissions.",
  MapRolePermission:
    "Grant and revoke permissions on roles in the access matrix.",
  ManagePolicies: "Author, edit, and publish authorization policies.",
  AssignRoles: "Grant and revoke role assignments for users.",
  ViewAudit: "View the audit trail and activity feed.",
  ReadOnlyView: "View configuration without making changes.",
};

const DELEGATED_ROLE_INFO: { role: string; label: string; desc: string }[] = [
  {
    role: "ApplicationAdmin",
    label: "Application Admin",
    desc: "Full administration of a single application.",
  },
  {
    role: "ReadOnlyViewer",
    label: "Read-only Viewer",
    desc: "Views configuration and audit without editing.",
  },
];

const ALL_CAPABILITIES = Object.keys(CAPABILITY_LABEL) as Capability[];

function capabilitiesForRole(role: string): Capability[] {
  return ALL_CAPABILITIES.filter((cap) => CAPABILITY_ROLES[cap].includes(role));
}

export function PlatformSettingsPage() {
  const [tab, setTab] = useState<"access" | "ai">("access");
  const ai = useAiConfig();
  return (
    <section className="page">
      <Breadcrumbs items={[{ label: "Platform" }, { label: "Settings" }]} />
      <header className="page-head">
        <div className="page-head-titles">
          <h1>Platform settings</h1>
          <p className="page-sub">
            Reference for the platform — the access model, and the AI assistance
            features available across every tenant and application.
          </p>
        </div>
      </header>

      <TabBar
        tabs={[
          { key: "access", label: "Access model" },
          { key: "ai", label: "AI features" },
        ]}
        active={tab}
        onChange={(key) => setTab(key as "access" | "ai")}
      />

      {tab === "access" && (
        <>
          <section className="ref-section">
            <h2 className="ref-heading">Platform roles</h2>
            <p className="ref-lead">
              Assigned in the identity provider. They apply across every
              application and tenant.
            </p>
            <div className="ref-card-grid">
              {PLATFORM_ROLE_INFO.map((r) => (
                <article key={r.claim} className="ref-card">
                  <h3>{r.label}</h3>
                  <code className="ref-claim">{r.claim}</code>
                  <p className="ref-desc">{r.desc}</p>
                  <p className="ref-access">{r.access}</p>
                </article>
              ))}
            </div>
          </section>

          <section className="ref-section">
            <h2 className="ref-heading">Delegated application roles</h2>
            <p className="ref-lead">
              Granted per application (as <code>{"{appId}:{role}"}</code>). Each
              role bundles the capabilities below.
            </p>
            <div className="ref-table-wrap">
              <table className="ref-table">
                <thead>
                  <tr>
                    <th scope="col">Role</th>
                    <th scope="col">What it does</th>
                    <th scope="col">Capabilities</th>
                  </tr>
                </thead>
                <tbody>
                  {DELEGATED_ROLE_INFO.map((r) => (
                    <tr key={r.role}>
                      <td>
                        <span className="ref-role-name">{r.label}</span>
                        <code className="ref-claim">{r.role}</code>
                      </td>
                      <td className="ref-desc">{r.desc}</td>
                      <td>
                        <span className="cap-chip-row">
                          {capabilitiesForRole(r.role).map((cap) => (
                            <span
                              key={cap}
                              className="cap-chip"
                              title={CAPABILITY_DESC[cap]}
                            >
                              {CAPABILITY_LABEL[cap]}
                            </span>
                          ))}
                        </span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </section>

          <section className="ref-section">
            <h2 className="ref-heading">Capabilities</h2>
            <p className="ref-lead">
              The individual permissions that roles grant, enforced by the API
              on every request.
            </p>
            <dl className="ref-dl">
              {ALL_CAPABILITIES.map((cap) => (
                <div key={cap} className="ref-dl-row">
                  <dt>{CAPABILITY_LABEL[cap]}</dt>
                  <dd>{CAPABILITY_DESC[cap]}</dd>
                </div>
              ))}
            </dl>
          </section>
        </>
      )}

      {tab === "ai" && (
        <section className="ref-section">
          <div className="panel-head-row">
            <h2 className="ref-heading">AI assistance</h2>
            <StatusChip value={ai.enabled ? "Enabled" : "Disabled"} />
          </div>
          {ai.enabled ? (
            <>
              <p className="ref-lead">
                Advisory features backed by the configured language model. Every
                feature is optional and controlled on the server; expand a row
                to see what it does, exactly where to find it, how it helps, and
                a worked example.
              </p>
              <div className="ai-cap-meta">
                <span className="ai-cap-meta-item">
                  <span className="muted">Provider</span>
                  <strong>{ai.provider ?? "—"}</strong>
                </span>
                {ai.model && (
                  <span className="ai-cap-meta-item">
                    <span className="muted">Model</span>
                    <strong>{ai.model}</strong>
                  </span>
                )}
                {ai.limits && (
                  <>
                    <span className="ai-cap-meta-item">
                      <span className="muted">Request timeout</span>
                      <strong>{ai.limits.requestTimeoutSeconds}s</strong>
                    </span>
                    <span className="ai-cap-meta-item">
                      <span className="muted">Max tokens</span>
                      <strong>{ai.limits.maxTokens}</strong>
                    </span>
                    <span className="ai-cap-meta-item">
                      <span className="muted">Temperature</span>
                      <strong>{ai.limits.temperature}</strong>
                    </span>
                  </>
                )}
              </div>
              <div className="ai-feature-list">
                {AI_FEATURE_LABELS.map((f) => {
                  const on = ai.features[f.key];
                  const temp = ai.featureTemperatures?.[f.key];
                  return (
                    <details key={f.key} className="ai-feature-item">
                      <summary>
                        <span className="ai-feature-name">{f.label}</span>
                        <span
                          className={`status-chip ${on ? "enabled" : "disabled"}`}
                        >
                          {on ? "On" : "Off"}
                        </span>
                      </summary>
                      <div className="ai-feature-detail">
                        <dl className="ai-feature-dl">
                          <dt>What it does</dt>
                          <dd>{f.what}</dd>
                          {on && temp != null && (
                            <>
                              <dt>Temperature</dt>
                              <dd>{temp}</dd>
                            </>
                          )}
                        </dl>
                        <div className="ai-feature-path-block">
                          <span className="ai-feature-detail-label">
                            Where to find it
                          </span>
                          <nav
                            className="ai-feature-path"
                            aria-label={`Path to ${f.label}`}
                          >
                            {f.path.map((step, i) => (
                              <span key={i} className="ai-feature-step">
                                {step}
                              </span>
                            ))}
                          </nav>
                          <p className="ai-feature-where">{f.where}</p>
                        </div>
                        <dl className="ai-feature-dl">
                          <dt>How it helps</dt>
                          <dd>{f.helps}</dd>
                          <dt>Example</dt>
                          <dd className="ai-feature-example">{f.example}</dd>
                        </dl>
                      </div>
                    </details>
                  );
                })}
              </div>
            </>
          ) : (
            <p className="explorer-none">
              AI assistance is turned off. Configure a provider on the server to
              enable the advisory features.
            </p>
          )}
        </section>
      )}
    </section>
  );
}


// ── Profile (the signed-in caller's own identity & access) ──────────────────

function initialsOf(name: string): string {
  const cleaned = name
    .split("@")[0]
    .replace(/[._-]+/g, " ")
    .trim();
  const parts = cleaned.split(/\s+/).filter(Boolean);
  if (parts.length === 0) return "?";
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}

export function ProfilePage() {
  const { user } = usePortal();
  const caps = useCapabilities();
  const applications = useApplications();

  const displayName = getDisplayName(user);
  const email = (user?.profile?.email as string | undefined) ?? displayName;
  const username =
    (user?.profile?.preferred_username as string | undefined) ?? undefined;

  const appName = (id: string) =>
    applications.data?.find((a) => a.applicationId === id)?.name ?? id;

  const myPlatformRoles = PLATFORM_ROLE_INFO.filter((r) =>
    caps.platformRoles.some(
      (held) => held.toLowerCase() === r.claim.toLowerCase(),
    ),
  );

  // Group the caller's per-application roles by application.
  const appGroups = useMemo(() => {
    const map = new Map<string, string[]>();
    for (const { applicationId, role } of caps.appRoles) {
      const list = map.get(applicationId) ?? [];
      if (!list.includes(role)) list.push(role);
      map.set(applicationId, list);
    }
    return Array.from(map.entries())
      .map(([applicationId, roles]) => ({ applicationId, roles }))
      .sort((a, b) =>
        appName(a.applicationId).localeCompare(appName(b.applicationId)),
      );
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [caps.appRoles, applications.data]);

  const roleLabel = (role: string) =>
    DELEGATED_ROLE_INFO.find((r) => r.role.toLowerCase() === role.toLowerCase())
      ?.label ?? role;

  // Capabilities the caller effectively holds (platform admin ⇒ everything).
  const effectiveCaps: Capability[] = caps.canManagePlatform
    ? ALL_CAPABILITIES
    : ALL_CAPABILITIES.filter((cap) =>
        caps.appRoles.some(({ role }) =>
          CAPABILITY_ROLES[cap].some(
            (r) => r.toLowerCase() === role.toLowerCase(),
          ),
        ),
      );

  return (
    <section className="page">
      <Breadcrumbs items={[{ label: "Platform" }, { label: "Profile" }]} />
      <header className="page-head">
        <div className="page-head-titles">
          <h1>Your profile</h1>
          <p className="page-sub">
            Who you are, the roles you hold, and what you can do in this
            platform.
          </p>
        </div>
      </header>

      <section className="profile-identity">
        <span className="profile-avatar" aria-hidden="true">
          {initialsOf(displayName)}
        </span>
        <div className="profile-identity-body">
          <h2 className="profile-name">{displayName}</h2>
          <p className="profile-email">{email}</p>
          <div className="profile-tags">
            {caps.canManagePlatform ? (
              <span className="profile-tag tag-admin">
                Platform administrator
              </span>
            ) : caps.canAccessAllApplications ? (
              <span className="profile-tag tag-audit">Global read-only</span>
            ) : (
              <span className="profile-tag tag-scoped">
                Delegated administrator
              </span>
            )}
            {username && username !== email && (
              <span className="profile-tag tag-muted">@{username}</span>
            )}
          </div>
        </div>
      </section>

      {myPlatformRoles.length > 0 && (
        <section className="ref-section">
          <h2 className="ref-heading">Platform roles</h2>
          <p className="ref-lead">
            Realm roles that apply across every application and tenant.
          </p>
          <div className="ref-card-grid">
            {myPlatformRoles.map((r) => (
              <article key={r.claim} className="ref-card">
                <h3>{r.label}</h3>
                <code className="ref-claim">{r.claim}</code>
                <p className="ref-desc">{r.desc}</p>
                <p className="ref-access">{r.access}</p>
              </article>
            ))}
          </div>
        </section>
      )}

      {appGroups.length > 0 && (
        <section className="ref-section">
          <h2 className="ref-heading">Application access</h2>
          <p className="ref-lead">
            Delegated roles granted to you on specific applications.
          </p>
          <div className="profile-app-grid">
            {appGroups.map((group) => (
              <article key={group.applicationId} className="profile-app-card">
                <div className="profile-app-head">
                  <span className="profile-app-icon" aria-hidden="true">
                    <AppIcon name="applications" size={18} />
                  </span>
                  <div>
                    <h3>{appName(group.applicationId)}</h3>
                    <code className="ref-claim">{group.applicationId}</code>
                  </div>
                </div>
                <ul className="profile-role-list">
                  {group.roles.map((role) => (
                    <li key={role}>
                      <span className="profile-role-name">
                        {roleLabel(role)}
                      </span>
                      <span className="cap-chip-row">
                        {capabilitiesForRole(role).map((cap) => (
                          <span
                            key={cap}
                            className="cap-chip"
                            title={CAPABILITY_DESC[cap]}
                          >
                            {CAPABILITY_LABEL[cap]}
                          </span>
                        ))}
                      </span>
                    </li>
                  ))}
                </ul>
              </article>
            ))}
          </div>
        </section>
      )}

      <section className="ref-section">
        <h2 className="ref-heading">What you can do</h2>
        <p className="ref-lead">
          {caps.canManagePlatform
            ? "As a platform administrator you hold every capability across all applications."
            : caps.canAccessAllApplications
              ? "You have read-only visibility across all applications."
              : "The capabilities your roles grant, enforced by the API on every request."}
        </p>
        {effectiveCaps.length > 0 ? (
          <dl className="ref-dl">
            {effectiveCaps.map((cap) => (
              <div key={cap} className="ref-dl-row">
                <dt>{CAPABILITY_LABEL[cap]}</dt>
                <dd>{CAPABILITY_DESC[cap]}</dd>
              </div>
            ))}
          </dl>
        ) : (
          <EmptyBlock
            title="No capabilities yet"
            hint="You do not currently hold any administrative roles. Contact a platform administrator to request access."
          />
        )}
      </section>
    </section>
  );
}
