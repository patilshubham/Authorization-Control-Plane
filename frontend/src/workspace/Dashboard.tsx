import { useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  useAssignments,
  useAuditEvents,
  usePermissions,
  usePolicies,
  useRoles,
} from "../api/hooks";
import type { Selection } from "./selection";
import { describeEvent, relativeTime, actorLabel } from "./activity";
import { getRuntimeConfig } from "../api/runtimeConfig";
import { Spinner } from "../components/primitives";
import {
  AreaTrend,
  BarDistribution,
  DonutChart,
  buildActivityTrend,
  useChartTheme,
} from "../components/charts";
import { VizModal } from "../components/viz/VizModal";
import { D3Tree } from "../components/viz/D3Tree";
import { buildAppAccessTree } from "../components/viz/graphModel";
import { nodeSelection } from "../components/viz/nodeSelection";
import { AppIcon } from "../components/icons";
import { useCapabilities } from "../capabilities";
import { ConfigAdvisor } from "./ConfigAdvisor";
import { SodPanel } from "./SodPanel";
import { ExpiringAccess } from "./ExpiringAccess";
import { appPaths } from "./nav";
type CreateKind = "role" | "permission" | "policy";

export function Dashboard({
  appId,
  appName,
  onSelect,
  onCreate,
}: {
  appId: string;
  appName: string;
  onSelect: (s: Selection) => void;
  onCreate: (k: CreateKind) => void;
}) {
  const roles = useRoles(appId);
  const permissions = usePermissions(appId);
  const policies = usePolicies(appId);
  const assignments = useAssignments(appId);
  const audit = useAuditEvents(appId);
  const navigate = useNavigate();
  const chart = useChartTheme();
  const caps = useCapabilities();
  const canCreateRole = caps.can("ManageRoles", appId);
  const canCreatePermission = caps.can("ManagePermissions", appId);
  const canCreatePolicy = caps.can("ManagePolicies", appId);
  const [graphOpen, setGraphOpen] = useState(false);

  const loading =
    roles.isLoading ||
    permissions.isLoading ||
    policies.isLoading ||
    assignments.isLoading;

  const roleList = roles.data ?? [];
  const permList = permissions.data ?? [];
  const policyList = policies.data ?? [];
  const assignList = assignments.data ?? [];
  const events = audit.data ?? [];

  const stats = useMemo(() => {
    const privileged = roleList.filter((r) => r.privileged).length;
    const activeAssignments = assignList.filter(
      (a) => a.state === "ACTIVE",
    ).length;
    const publishedPolicies = policyList.filter(
      (p) => p.state === "PUBLISHED",
    ).length;
    const draftPolicies = policyList.length - publishedPolicies;
    const denyPolicies = policyList.filter((p) => p.effect === "DENY").length;
    const allowPolicies = policyList.length - denyPolicies;

    const riskCounts: Record<string, number> = {
      LOW: 0,
      MEDIUM: 0,
      HIGH: 0,
      CRITICAL: 0,
    };
    for (const r of roleList) {
      const key = (r.riskLevel ?? "MEDIUM").toUpperCase();
      riskCounts[key] = (riskCounts[key] ?? 0) + 1;
    }

    return {
      privileged,
      activeAssignments,
      publishedPolicies,
      draftPolicies,
      denyPolicies,
      allowPolicies,
      riskCounts,
    };
  }, [roleList, assignList, policyList]);

  if (loading) {
    return (
      <article className="inspector">
        <Spinner label="Loading dashboard…" />
      </article>
    );
  }

  const recent = events.slice(0, 8);
  const riskTotal = Object.values(stats.riskCounts).reduce((a, b) => a + b, 0);
  const riskData = (["CRITICAL", "HIGH", "MEDIUM", "LOW"] as const).map(
    (level) => ({
      label: level.charAt(0) + level.slice(1).toLowerCase(),
      value: stats.riskCounts[level] ?? 0,
      color: chart.risk[level],
    }),
  );
  const postureData = [
    { label: "Allow", value: stats.allowPolicies, color: chart.success },
    { label: "Deny", value: stats.denyPolicies, color: chart.danger },
  ];
  const activityTrend = buildActivityTrend(
    events,
    getRuntimeConfig().ui.activityTrendDays,
  );
  const livePct =
    policyList.length > 0
      ? Math.round((stats.publishedPolicies / policyList.length) * 100)
      : 0;

  const accessTree = buildAppAccessTree(
    appId,
    appName,
    roleList,
    permList,
    policyList,
  );
  const hasGraph = roleList.length > 0;

  return (
    <article className="dashboard">
      <header className="dashboard-head">
        <div>
          <h1>{appName}</h1>
          <p className="muted">Authorization overview</p>
        </div>
        <div className="dashboard-quick">
          {canCreateRole && (
            <button
              type="button"
              className="btn-secondary"
              onClick={() => onCreate("role")}
            >
              New role
            </button>
          )}
          {canCreatePermission && (
            <button
              type="button"
              className="btn-secondary"
              onClick={() => onCreate("permission")}
            >
              New permission
            </button>
          )}
          {canCreatePolicy && (
            <button
              type="button"
              className="btn-primary"
              onClick={() => onCreate("policy")}
            >
              New policy
            </button>
          )}
        </div>
      </header>

      <section className="dash-top">
        <div className="dash-kpis" aria-label="Key metrics">
          <KpiTile
            label="Roles"
            value={roleList.length}
            sub={`${stats.privileged} privileged`}
            tone="accent"
            icon="roles"
            onClick={() => navigate(appPaths.roles(appId))}
          />
          <KpiTile
            label="Permissions"
            value={permList.length}
            sub="governed resources"
            tone="neutral"
            icon="permissions"
            onClick={() => navigate(appPaths.permissions(appId))}
          />
          <KpiTile
            label="Policies"
            value={policyList.length}
            sub={`${stats.publishedPolicies} live · ${stats.draftPolicies} draft`}
            tone="neutral"
            icon="policies"
            onClick={() =>
              navigate(
                stats.draftPolicies > 0
                  ? `${appPaths.policies(appId)}?state=DRAFT`
                  : appPaths.policies(appId),
              )
            }
          />
          <KpiTile
            label="Active assignments"
            value={stats.activeAssignments}
            sub={`${assignList.length} total`}
            tone="success"
            icon="active"
            onClick={() => onSelect({ kind: "access" })}
          />
        </div>

        <div className="dash-card dash-access">
          <div className="dash-card-head">
            <h2>Access graph</h2>
            <button
              type="button"
              className="mini-btn ghost"
              disabled={!hasGraph}
              onClick={() => setGraphOpen(true)}
            >
              Expand
            </button>
          </div>
          {!hasGraph ? (
            <p className="muted">
              Create a role to see the access graph for {appName}.
            </p>
          ) : (
            <div className="dash-graph-preview">
              <D3Tree
                data={accessTree}
                height={210}
                onSelect={(node) => {
                  const sel = nodeSelection(node);
                  if (sel) onSelect(sel);
                }}
                ariaLabel={`${appName} access graph preview`}
              />
            </div>
          )}
        </div>
      </section>

      <section className="dashboard-grid">
        <div className="dash-card dash-card-wide">
          <div className="dash-card-head">
            <h2>Activity trend</h2>
            <span className="muted">Last 14 days</span>
          </div>
          <AreaTrend data={activityTrend} height={180} />
        </div>

        <ExpiringAccess appId={appId} onSelect={onSelect} />

        <ConfigAdvisor appId={appId} onSelect={onSelect} />

        <SodPanel appId={appId} onSelect={onSelect} />

        <div className="dash-card">
          <div className="dash-card-head">
            <h2>Role risk distribution</h2>
            <span className="muted">{riskTotal} roles</span>
          </div>
          {riskTotal === 0 ? (
            <p className="muted">No roles yet.</p>
          ) : (
            <BarDistribution data={riskData} height={200} />
          )}
        </div>

        <div className="dash-card">
          <div className="dash-card-head">
            <h2>Policy posture</h2>
            <span className="muted">{policyList.length} policies</span>
          </div>
          {policyList.length === 0 ? (
            <p className="muted">No policies yet.</p>
          ) : (
            <div className="donut-wrap">
              <DonutChart
                data={postureData}
                centerLabel={`${livePct}%`}
                centerSub="live"
                size={150}
              />
              <ul className="legend">
                <li>
                  <span className="legend-dot seg-allow" /> Allow{" "}
                  <strong>{stats.allowPolicies}</strong>
                </li>
                <li>
                  <span className="legend-dot seg-deny" /> Deny{" "}
                  <strong>{stats.denyPolicies}</strong>
                </li>
                <li>
                  <span className="legend-dot seg-draft" /> Draft{" "}
                  <strong>{stats.draftPolicies}</strong>
                </li>
              </ul>
            </div>
          )}
        </div>

        <div className="dash-card dash-card-wide">
          <div className="dash-card-head">
            <h2>Recent activity</h2>
            <button
              type="button"
              className="mini-btn ghost"
              onClick={() => onSelect({ kind: "activity" })}
            >
              View all
            </button>
          </div>
          {audit.isLoading ? (
            <Spinner label="Loading activity…" />
          ) : recent.length === 0 ? (
            <p className="muted">No activity recorded yet.</p>
          ) : (
            <ul className="dash-activity">
              {recent.map((e, i) => {
                const meta = describeEvent(e.eventType);
                return (
                  <li key={e.eventId ?? i}>
                    <span
                      className={`activity-icon tone-${meta.tone}`}
                      aria-hidden="true"
                    >
                      {meta.icon}
                    </span>
                    <span className="activity-main">
                      <span className="activity-label">{meta.label}</span>
                      <span className="muted">
                        {actorLabel(e.actorEmail, e.actorRole)}
                        {e.targetSubjectEmail
                          ? ` → ${e.targetSubjectEmail}`
                          : ""}
                      </span>
                    </span>
                    <span className="activity-time muted">
                      {relativeTime(e.timestamp)}
                    </span>
                  </li>
                );
              })}
            </ul>
          )}
        </div>
      </section>

      <VizModal
        title={`${appName} access graph`}
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
          data={accessTree}
          height={640}
          onSelect={(node) => {
            const sel = nodeSelection(node);
            if (sel) {
              onSelect(sel);
              setGraphOpen(false);
            }
          }}
          ariaLabel={`${appName} access graph`}
        />
      </VizModal>
    </article>
  );
}

function KpiTile({
  label,
  value,
  sub,
  tone,
  icon,
  onClick,
}: {
  label: string;
  value: number;
  sub: string;
  tone: "accent" | "success" | "neutral";
  icon: string;
  onClick?: () => void;
}) {
  return (
    <button type="button" className={`kpi-tile kpi-${tone}`} onClick={onClick}>
      <span className="kpi-ico">
        <AppIcon name={icon} size={24} />
      </span>
      <span className="kpi-label">{label}</span>
      <span className="kpi-value">{value}</span>
      <span className="kpi-sub">{sub}</span>
    </button>
  );
}
