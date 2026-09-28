import {
  createBrowserRouter,
  Navigate,
  useSearchParams,
} from "react-router-dom";
import { PlatformShell } from "./shells/PlatformShell";
import { AppWorkspaceShell } from "./shells/AppWorkspaceShell";
import { useApplications } from "./api/hooks";
import { useCapabilities } from "./capabilities";
import { Spinner } from "./components/primitives";
import {
  AccessLensPage,
  ApplicationsPage,
  AuditPage,
  AiUsagePage,
  AskAiPage,
  PlatformOverviewPage,
  PlatformSettingsPage,
  ProfilePage,
  TenantDetailPage,
  TenantsPage,
  UserDetailPage,
  UsersPage,
} from "./pages/platform/PlatformPages";
import {
  AppDashboardPage,
  AppSettingsPage,
  ActivityPage,
  AssignmentsPage,
  CertificationsPage,
  DecisionsPage,
  IdentityPage,
  MatrixPage,
  PermissionDetailPage,
  PermissionsPage,
  PolicyDetailPage,
  PoliciesPage,
  ReferenceDataPage,
  RoleDetailPage,
  RolesPage,
  SimulatorPage,
} from "./pages/app/AppPages";
import { platformPaths, selectionToAppPath, appPaths } from "./workspace/nav";
import { selectionFromParams } from "./workspace/location";

// Redirect legacy query-param URLs (?app=&view=&key=&email=) to the new
// path-based routes so old bookmarks and deep links keep working.
function LegacyRedirect() {
  const [params] = useSearchParams();
  const app = params.get("app");
  const view = params.get("view");
  if (app) {
    const selection = selectionFromParams(
      view ?? "dashboard",
      params.get("key") ?? "",
      params.get("email") ?? "",
    );
    return <Navigate to={selectionToAppPath(app, selection)} replace />;
  }
  if (view === "tenants")
    return <Navigate to={platformPaths.tenants} replace />;
  if (view === "users") return <Navigate to={platformPaths.users} replace />;
  if (view === "explorer")
    return <Navigate to={platformPaths.accessLens} replace />;
  return null;
}

// Landing decision for a clean "/" visit (no legacy params). A delegated
// (application-scoped) administrator has no platform visibility and is taken
// straight into their application workspace. Platform principals land on the
// platform overview.
function IndexLanding() {
  const [params] = useSearchParams();
  const caps = useCapabilities();
  const applications = useApplications();

  // Legacy query-param deep links take precedence and never auto-enter.
  if (params.get("app") || params.get("view")) return <LegacyRedirect />;

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
  if (!caps.canAccessAllApplications && apps.length >= 1) {
    return <Navigate to={appPaths.dashboard(apps[0].applicationId)} replace />;
  }
  return <Navigate to={platformPaths.overview} replace />;
}

export const router = createBrowserRouter([
  { path: "/", element: <IndexLanding /> },
  {
    path: "/platform",
    element: <PlatformShell />,
    children: [
      { index: true, element: <PlatformOverviewPage /> },
      { path: "applications", element: <ApplicationsPage /> },
      { path: "tenants", element: <TenantsPage /> },
      { path: "tenants/:tenantId", element: <TenantDetailPage /> },
      { path: "users", element: <UsersPage /> },
      { path: "users/:email", element: <UserDetailPage /> },
      { path: "access-lens", element: <AccessLensPage /> },
      { path: "audit", element: <AuditPage /> },
      { path: "ai-usage", element: <AiUsagePage /> },
      { path: "ask-ai", element: <AskAiPage /> },
      { path: "settings", element: <PlatformSettingsPage /> },
      { path: "profile", element: <ProfilePage /> },
    ],
  },
  {
    path: "/app/:appId",
    element: <AppWorkspaceShell />,
    children: [
      { index: true, element: <AppDashboardPage /> },
      { path: "roles", element: <RolesPage /> },
      { path: "roles/:roleKey", element: <RoleDetailPage /> },
      { path: "permissions", element: <PermissionsPage /> },
      { path: "permissions/:permissionKey", element: <PermissionDetailPage /> },
      { path: "policies", element: <PoliciesPage /> },
      { path: "policies/:policyKey", element: <PolicyDetailPage /> },
      { path: "reference-data", element: <ReferenceDataPage /> },
      { path: "matrix", element: <MatrixPage /> },
      { path: "assignments", element: <AssignmentsPage /> },
      { path: "identity", element: <IdentityPage /> },
      { path: "simulator", element: <SimulatorPage /> },
      { path: "decisions", element: <DecisionsPage /> },
      { path: "activity", element: <ActivityPage /> },
      { path: "certifications", element: <CertificationsPage /> },
      { path: "settings", element: <AppSettingsPage /> },
    ],
  },
  { path: "*", element: <Navigate to={platformPaths.overview} replace /> },
]);
