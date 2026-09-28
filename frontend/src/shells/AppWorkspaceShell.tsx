import { useEffect, useMemo, useState } from "react";
import { NavLink, Outlet, useNavigate, useParams } from "react-router-dom";
import {
  useApplications,
  usePermissions,
  usePolicies,
  useRoles,
  useTenants,
} from "../api/hooks";
import { useToast } from "../components/Toast";
import { useCapabilities } from "../capabilities";
import { useAiFeature } from "../api/aiConfig";
import { Kbd, Spinner } from "../components/primitives";
import { AppIcon } from "../components/icons";
import { CommandPalette, type PaletteItem } from "../workspace/CommandPalette";
import { CreateForms, type CreateKind } from "../workspace/CreateForms";
import { ScopeBadge } from "../scope/Scope";
import { ThemeToggle } from "../theme/ThemeToggle";
import { NotificationsBell } from "../components/NotificationsBell";
import { UserMenu } from "./UserMenu";
import { appPaths, platformPaths } from "../workspace/nav";
import type { AppOutletContext } from "./appContext";

const isMac =
  typeof navigator !== "undefined" &&
  /Mac|iPhone|iPad/.test(navigator.platform);

const APP_LINKS: Array<{
  path: (appId: string) => string;
  label: string;
  icon: string;
  end?: boolean;
}> = [
  {
    path: appPaths.dashboard,
    label: "Dashboard",
    icon: "dashboard",
    end: true,
  },
  { path: appPaths.roles, label: "Roles", icon: "roles" },
  { path: appPaths.permissions, label: "Permissions", icon: "permissions" },
  { path: appPaths.policies, label: "Policies", icon: "policies" },
  { path: appPaths.referenceData, label: "Reference data", icon: "reference" },
  { path: appPaths.matrix, label: "Access matrix", icon: "matrix" },
  { path: appPaths.assignments, label: "Assignments", icon: "assignments" },
  { path: appPaths.identity, label: "Identity providers", icon: "identity" },
  { path: appPaths.simulator, label: "Simulator", icon: "simulator" },
  { path: appPaths.decisions, label: "Decisions", icon: "activity" },
  { path: appPaths.activity, label: "Activity", icon: "activity" },
  { path: appPaths.certifications, label: "Certifications", icon: "audit" },
  { path: appPaths.settings, label: "Settings", icon: "settings" },
];

export function AppWorkspaceShell() {
  const { appId = "" } = useParams();
  const navigate = useNavigate();
  const toast = useToast();
  const caps = useCapabilities();

  const applications = useApplications();
  const tenants = useTenants();
  const roles = useRoles(appId);
  const permissions = usePermissions(appId);
  const policies = usePolicies(appId);

  const [paletteOpen, setPaletteOpen] = useState(false);
  const [creating, setCreating] = useState<CreateKind | null>(null);

  const app = applications.data?.find((a) => a.applicationId === appId);
  const tenantName = (id: string | null | undefined) =>
    (id && tenants.data?.find((t) => t.tenantId === id)?.name) || "Unassigned";

  // Guard: an unknown or unauthorized application returns to the platform
  // applications list with an explanatory toast rather than a blank workspace.
  useEffect(() => {
    if (applications.isSuccess && !app) {
      toast.error(`Application “${appId}” is not available.`);
      navigate(platformPaths.applications, { replace: true });
    }
  }, [applications.isSuccess, app, appId, navigate, toast]);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        setPaletteOpen((o) => !o);
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  const groupedApps = useMemo(() => {
    const groups = new Map<
      string,
      { key: string; label: string; apps: typeof applications.data }
    >();
    for (const a of applications.data ?? []) {
      const key = a.tenantId ?? "__none__";
      if (!groups.has(key))
        groups.set(key, { key, label: tenantName(a.tenantId), apps: [] });
      groups.get(key)!.apps!.push(a);
    }
    return [...groups.values()].sort((x, y) => x.label.localeCompare(y.label));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [applications.data, tenants.data]);

  const canCreateRole = caps.can("ManageRoles", appId);
  const canCreatePermission = caps.can("ManagePermissions", appId);
  const canCreatePolicy = caps.can("ManagePolicies", appId);
  const sodEnabled = useAiFeature("sodAnalysis");

  const paletteItems = useMemo<PaletteItem[]>(() => {
    const items: PaletteItem[] = [
      {
        id: "goto-dashboard",
        group: "Go to",
        label: "Dashboard",
        keywords: "overview home",
        run: () => navigate(appPaths.dashboard(appId)),
      },
      {
        id: "goto-roles",
        group: "Go to",
        label: "Roles",
        run: () => navigate(appPaths.roles(appId)),
      },
      {
        id: "goto-permissions",
        group: "Go to",
        label: "Permissions",
        run: () => navigate(appPaths.permissions(appId)),
      },
      {
        id: "goto-policies",
        group: "Go to",
        label: "Policies",
        run: () => navigate(appPaths.policies(appId)),
      },
      {
        id: "goto-reference-data",
        group: "Go to",
        label: "Reference data",
        keywords: "lookup lists reference conditions",
        run: () => navigate(appPaths.referenceData(appId)),
      },
      {
        id: "goto-matrix",
        group: "Go to",
        label: "Access matrix",
        run: () => navigate(appPaths.matrix(appId)),
      },
      {
        id: "goto-assignments",
        group: "Go to",
        label: "Assignments",
        run: () => navigate(appPaths.assignments(appId)),
      },
      {
        id: "goto-identity",
        group: "Go to",
        label: "Identity providers",
        run: () => navigate(appPaths.identity(appId)),
      },
      {
        id: "goto-simulator",
        group: "Go to",
        label: "Simulator",
        run: () => navigate(appPaths.simulator(appId)),
      },
      {
        id: "goto-activity",
        group: "Go to",
        label: "Activity",
        run: () => navigate(appPaths.activity(appId)),
      },
      {
        id: "goto-settings",
        group: "Go to",
        label: "Settings",
        run: () => navigate(appPaths.settings(appId)),
      },
    ];
    if (caps.canAccessAllApplications) {
      items.push({
        id: "exit-platform",
        group: "Scope",
        label: "Exit to platform",
        keywords: "leave global back",
        run: () => navigate(platformPaths.overview),
      });
    }
    if (canCreateRole)
      items.push({
        id: "new-role",
        group: "Create",
        label: "New role",
        run: () => setCreating("role"),
      });
    if (canCreatePermission)
      items.push({
        id: "new-permission",
        group: "Create",
        label: "New permission",
        run: () => setCreating("permission"),
      });
    if (canCreatePolicy)
      items.push({
        id: "new-policy",
        group: "Create",
        label: "New policy",
        run: () => setCreating("policy"),
      });
    if (sodEnabled)
      items.push({
        id: "review-sod",
        group: "Go to",
        label: "Separation of duties",
        keywords: "sod conflict toxic combination segregation ai",
        run: () => navigate(appPaths.dashboard(appId)),
      });
    for (const a of applications.data ?? []) {
      if (a.applicationId === appId) continue;
      items.push({
        id: `switch-${a.applicationId}`,
        group: "Switch application",
        label: a.name,
        hint: tenantName(a.tenantId),
        run: () => navigate(appPaths.dashboard(a.applicationId)),
      });
    }
    for (const role of roles.data ?? []) {
      items.push({
        id: `role-${role.roleKey}`,
        group: "Roles",
        label: role.name,
        hint: role.roleKey,
        run: () => navigate(appPaths.role(appId, role.roleKey)),
      });
    }
    for (const perm of permissions.data ?? []) {
      items.push({
        id: `perm-${perm.permissionKey}`,
        group: "Permissions",
        label: perm.permissionKey,
        hint: `${perm.resource}·${perm.action}`,
        run: () => navigate(appPaths.permission(appId, perm.permissionKey)),
      });
    }
    for (const policy of policies.data ?? []) {
      items.push({
        id: `policy-${policy.policyKey}`,
        group: "Policies",
        label: policy.policyKey,
        hint: policy.effect,
        run: () => navigate(appPaths.policy(appId, policy.policyKey)),
      });
    }
    return items;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [
    appId,
    applications.data,
    tenants.data,
    roles.data,
    permissions.data,
    policies.data,
    canCreateRole,
    canCreatePermission,
    canCreatePolicy,
    sodEnabled,
    caps.canAccessAllApplications,
    navigate,
  ]);

  if (!app) {
    return (
      <main className="login-shell">
        <section className="login-panel" aria-busy="true">
          <Spinner label="Opening application…" />
        </section>
      </main>
    );
  }

  const currentTenantName = tenantName(app.tenantId);
  const outletContext: AppOutletContext = {
    appId,
    app,
    tenantName: currentTenantName,
    startCreate: setCreating,
  };

  return (
    <div className="console">
      <header className="console-topbar">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true">
            <AppIcon name="brand" size={22} />
          </span>
          <span className="brand-text">
            <strong>Authorization Control Plane</strong>
            <span className="brand-sub">{app.name}</span>
          </span>
        </div>

        <button
          type="button"
          className="palette-trigger"
          onClick={() => setPaletteOpen(true)}
        >
          <span className="palette-trigger-icon" aria-hidden="true">
            <AppIcon name="search" size={16} />
          </span>
          Search or jump to…
          <span className="palette-trigger-kbd">
            <Kbd>{isMac ? "⌘" : "Ctrl"}</Kbd>
            <Kbd>K</Kbd>
          </span>
        </button>

        <div className="topbar-right">
          <NotificationsBell appId={appId} />
          <ThemeToggle />
          <UserMenu />
        </div>
      </header>

      <div className="console-body">
        <nav className="explorer" aria-label="Application navigation">
          <div className="explorer-scopebar">
            {caps.canAccessAllApplications && (
              <button
                type="button"
                className="scope-exit"
                onClick={() => navigate(platformPaths.overview)}
                title="Exit to platform"
              >
                ← Go Back to Platform
              </button>
            )}
            <ScopeBadge scope="application" />
            <label className="app-switcher">
              <span className="visually-hidden">Switch application</span>
              <select
                value={appId}
                onChange={(e) => navigate(appPaths.dashboard(e.target.value))}
                aria-label="Switch application"
              >
                {groupedApps.map((group) => (
                  <optgroup key={group.key} label={group.label}>
                    {(group.apps ?? []).map((a) => (
                      <option key={a.applicationId} value={a.applicationId}>
                        {a.name}
                      </option>
                    ))}
                  </optgroup>
                ))}
              </select>
            </label>
          </div>

          <ul className="explorer-links">
            {APP_LINKS.map((link) => (
              <li key={link.label}>
                <NavLink
                  to={link.path(appId)}
                  end={link.end}
                  className={({ isActive }) =>
                    `explorer-link${isActive ? " active" : ""}`
                  }
                >
                  <span className="explorer-link-icon" aria-hidden="true">
                    <AppIcon name={link.icon} size={18} />
                  </span>
                  {link.label}
                </NavLink>
              </li>
            ))}
          </ul>
        </nav>

        <main className="console-main" aria-live="polite">
          <Outlet context={outletContext} />
        </main>
      </div>

      <CommandPalette
        open={paletteOpen}
        items={paletteItems}
        onClose={() => setPaletteOpen(false)}
      />
      <CreateForms
        appId={appId}
        kind={creating}
        onClose={() => setCreating(null)}
      />
    </div>
  );
}
