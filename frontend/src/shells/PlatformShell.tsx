import { useEffect, useMemo, useState } from "react";
import { NavLink, Navigate, Outlet, useNavigate } from "react-router-dom";
import { useApplications, useTenants, useUsersDirectory } from "../api/hooks";
import { useCapabilities } from "../capabilities";
import { Kbd, Spinner } from "../components/primitives";
import { AppIcon } from "../components/icons";
import { CommandPalette, type PaletteItem } from "../workspace/CommandPalette";
import { ApplicationForm } from "../workspace/CreateForms";
import { ScopeBadge } from "../scope/Scope";
import { ThemeToggle } from "../theme/ThemeToggle";
import { UserMenu } from "./UserMenu";
import { appPaths, platformPaths } from "../workspace/nav";

const isMac =
  typeof navigator !== "undefined" &&
  /Mac|iPhone|iPad/.test(navigator.platform);

const PLATFORM_LINKS: Array<{
  to: string;
  label: string;
  icon: string;
  end?: boolean;
}> = [
  {
    to: platformPaths.overview,
    label: "Overview",
    icon: "overview",
    end: true,
  },
  {
    to: platformPaths.applications,
    label: "Applications",
    icon: "applications",
  },
  { to: platformPaths.tenants, label: "Tenants", icon: "tenants" },
  { to: platformPaths.users, label: "Users", icon: "users" },
  { to: platformPaths.accessLens, label: "Access lens", icon: "lens" },
  { to: platformPaths.audit, label: "Audit", icon: "audit" },
  { to: platformPaths.aiUsage, label: "AI usage", icon: "overview" },
  { to: platformPaths.askAi, label: "Ask AI", icon: "sparkles" },
  { to: platformPaths.settings, label: "Platform settings", icon: "settings" },
];

export function PlatformShell() {
  const navigate = useNavigate();
  const caps = useCapabilities();
  const [paletteOpen, setPaletteOpen] = useState(false);
  const [creatingApp, setCreatingApp] = useState(false);

  const applications = useApplications();
  const tenants = useTenants();
  const users = useUsersDirectory();

  const tenantName = (id: string | null | undefined) =>
    (id && tenants.data?.find((t) => t.tenantId === id)?.name) || "Unassigned";

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

  const paletteItems = useMemo<PaletteItem[]>(() => {
    const items: PaletteItem[] = [
      {
        id: "goto-overview",
        group: "Platform",
        label: "Overview",
        keywords: "home metrics dashboard",
        run: () => navigate(platformPaths.overview),
      },
      {
        id: "goto-applications",
        group: "Platform",
        label: "Applications",
        keywords: "apps catalog",
        run: () => navigate(platformPaths.applications),
      },
      {
        id: "goto-tenants",
        group: "Platform",
        label: "Tenants",
        keywords: "organization owner",
        run: () => navigate(platformPaths.tenants),
      },
      {
        id: "goto-users",
        group: "Platform",
        label: "Users",
        keywords: "subjects people access",
        run: () => navigate(platformPaths.users),
      },
      {
        id: "goto-lens",
        group: "Platform",
        label: "Access lens",
        keywords: "hierarchy tree explorer",
        run: () => navigate(platformPaths.accessLens),
      },
      {
        id: "goto-audit",
        group: "Platform",
        label: "Audit",
        keywords: "log history events",
        run: () => navigate(platformPaths.audit),
      },
      {
        id: "goto-ai-usage",
        group: "Platform",
        label: "AI usage",
        keywords: "ai assistant tokens cost invocations telemetry",
        run: () => navigate(platformPaths.aiUsage),
      },
      {
        id: "goto-ask-ai",
        group: "Platform",
        label: "Ask AI",
        keywords: "ai chat ask access search question natural language",
        run: () => navigate(platformPaths.askAi),
      },
      {
        id: "goto-settings",
        group: "Platform",
        label: "Platform settings",
        keywords: "configuration",
        run: () => navigate(platformPaths.settings),
      },
    ];
    if (caps.canManagePlatform) {
      items.push({
        id: "new-application",
        group: "Create",
        label: "New application",
        run: () => setCreatingApp(true),
      });
    }
    for (const app of applications.data ?? []) {
      items.push({
        id: `enter-${app.applicationId}`,
        group: "Open application",
        label: app.name,
        hint: tenantName(app.tenantId),
        keywords: "enter workspace app",
        run: () => navigate(appPaths.dashboard(app.applicationId)),
      });
    }
    for (const tenant of tenants.data ?? []) {
      items.push({
        id: `tenant-${tenant.tenantId}`,
        group: "Tenants",
        label: tenant.name,
        hint: tenant.tenantId,
        keywords: "tenant organization",
        run: () => navigate(platformPaths.tenant(tenant.tenantId)),
      });
    }
    for (const user of users.data ?? []) {
      items.push({
        id: `user-${user.email}`,
        group: "Users",
        label: user.email,
        keywords: "user subject person access",
        run: () => navigate(platformPaths.user(user.email)),
      });
    }
    return items;
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [applications.data, tenants.data, users.data, caps.canManagePlatform, navigate]);

  // Platform administration is reserved for platform-scoped principals
  // (Super Admin and Platform Read-only Viewer). A delegated, application-scoped
  // admin has no visibility here and is routed into their own workspace.
  if (!caps.canAccessAllApplications) {
    if (applications.isLoading) {
      return (
        <main className="login-shell">
          <section className="login-panel" aria-busy="true">
            <Spinner label="Preparing your workspace…" />
          </section>
        </main>
      );
    }
    const apps = applications.data ?? [];
    if (apps.length > 0) {
      return (
        <Navigate to={appPaths.dashboard(apps[0].applicationId)} replace />
      );
    }
    return (
      <main className="login-shell">
        <section className="login-panel">
          <h1>No workspaces available</h1>
          <p>
            Your account doesn’t have access to any application yet. Contact a
            platform administrator.
          </p>
        </section>
      </main>
    );
  }

  return (
    <div className="console">
      <header className="console-topbar">
        <div className="brand">
          <span className="brand-mark" aria-hidden="true">
            <AppIcon name="brand" size={22} />
          </span>
          <span className="brand-text">
            <strong>Authorization Control Plane</strong>
            <span className="brand-sub">Platform administration</span>
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
          <ThemeToggle />
          <UserMenu />
        </div>
      </header>

      <div className="console-body">
        <nav className="explorer" aria-label="Platform navigation">
          <div className="explorer-scopebar">
            <ScopeBadge scope="platform" />
          </div>

          <ul className="explorer-links">
            {PLATFORM_LINKS.map((link) => (
              <li key={link.to}>
                <NavLink
                  to={link.to}
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
          <Outlet />
        </main>
      </div>

      <CommandPalette
        open={paletteOpen}
        items={paletteItems}
        onClose={() => setPaletteOpen(false)}
      />
      {creatingApp && <ApplicationForm onClose={() => setCreatingApp(false)} />}
    </div>
  );
}
