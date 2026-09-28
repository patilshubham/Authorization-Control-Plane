import {
  MutationCache,
  QueryClient,
  QueryClientProvider,
} from "@tanstack/react-query";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import { RouterProvider } from "react-router-dom";
import type { User } from "oidc-client-ts";
import "./App.css";
import {
  getUser,
  handleRedirectCallback,
  login,
  logout,
  trySilentSignin,
} from "./auth";
import { userFacingError } from "./apiClient";
import { CapabilitiesProvider } from "./capabilities";
import { useApplications } from "./api/hooks";
import { ToastProvider, useToast } from "./components/Toast";
import { ThemeProvider } from "./theme/ThemeProvider";
import { PortalContext } from "./shells/PortalContext";
import { router } from "./router";
import { usePortalConfig } from "./api/aiConfig";
import { getRuntimeConfig } from "./api/runtimeConfig";

// ── QueryClient ───────────────────────────────────────────────────────────────

// A QueryClient whose mutation cache surfaces every failed write as an error toast,
// so create/edit/delete/grant actions can never fail silently.
function useQueryClientWithToasts(): QueryClient {
  const toast = useToast();
  return useMemo(
    () =>
      new QueryClient({
        defaultOptions: {
          queries: {
            retry: 1,
            staleTime: () => getRuntimeConfig().cache.defaultStaleMs,
          },
        },
        mutationCache: new MutationCache({
          onError: (error) => toast.error(userFacingError(error)),
        }),
      }),
    [toast],
  );
}

// ── Root ──────────────────────────────────────────────────────────────────────

function App() {
  return (
    <ThemeProvider>
      <ToastProvider>
        <AppWithQueryClient />
      </ToastProvider>
    </ThemeProvider>
  );
}

function AppWithQueryClient() {
  const queryClient = useQueryClientWithToasts();
  return (
    <QueryClientProvider client={queryClient}>
      <Portal />
    </QueryClientProvider>
  );
}

// ── Portal shell ──────────────────────────────────────────────────────────────

function Portal() {
  const [authState, setAuthState] = useState<
    "loading" | "signedOut" | "signedIn"
  >("loading");
  const [user, setUser] = useState<User | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const callbackUser = await handleRedirectCallback();
        // Tokens are memory-only, so a plain reload starts with no user. Fall back to a
        // no-interaction silent sign-in (Keycloak SSO cookie) before prompting to log in.
        let current = callbackUser ?? (await getUser());
        if (!current || current.expired) {
          current = await trySilentSignin();
        }
        if (cancelled) {
          return;
        }
        if (current && !current.expired) {
          setUser(current);
          setAuthState("signedIn");
        } else {
          setAuthState("signedOut");
        }
      } catch {
        if (!cancelled) {
          setAuthState("signedOut");
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const portalValue = useMemo(
    () => ({ user, onLogout: () => void logout() }),
    [user],
  );

  if (authState === "loading") {
    return (
      <main className="login-shell" aria-busy="true">
        <section className="login-card login-card--status" role="status">
          <span className="login-spinner" aria-hidden="true" />
          <p className="eyebrow">Authorization Control Plane</p>
          <h1>Signing you in…</h1>
          <p className="login-sub">
            Restoring your secure session with Keycloak.
          </p>
        </section>
      </main>
    );
  }

  if (authState === "signedOut") {
    return (
      <main className="login-shell">
        <div className="login-split">
          <section className="login-hero" aria-hidden="true">
            <p className="eyebrow login-hero-brand">
              Authorization Control Plane
            </p>
            <h2 className="login-hero-title">
              Least-privilege access, under one roof.
            </h2>
            <p className="login-hero-lede">
              Manage tenants, applications, roles, policies, and audit — all
              governed by a single, consistent authorization model.
            </p>
            <ul className="login-points">
              <li>Fine-grained role &amp; policy administration</li>
              <li>Complete audit trail for every change</li>
              <li>OIDC single sign-on backed by Keycloak</li>
            </ul>
          </section>

          <section className="login-card" aria-labelledby="login-title">
            <span className="login-mark" aria-hidden="true">
              <svg viewBox="0 0 24 24" width="24" height="24" role="img">
                <path
                  d="M12 2.5 4.5 5.5v5.2c0 4.6 3.2 8.9 7.5 10.3 4.3-1.4 7.5-5.7 7.5-10.3V5.5L12 2.5Z"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="1.6"
                  strokeLinejoin="round"
                />
                <path
                  d="m8.8 12 2.2 2.2 4.2-4.4"
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="1.6"
                  strokeLinecap="round"
                  strokeLinejoin="round"
                />
              </svg>
            </span>
            <p className="eyebrow">Welcome back</p>
            <h1 id="login-title">Govern access with local OIDC</h1>
            <p className="login-sub">
              Sign in with your Keycloak identity to continue to the control
              plane.
            </p>
            <button
              type="button"
              className="login-cta"
              onClick={() => void login()}
            >
              Sign in with Keycloak
            </button>
            <p className="login-fineprint">
              You&apos;ll be redirected to Keycloak to authenticate securely.
            </p>
          </section>
        </div>
      </main>
    );
  }

  return (
    <TenantAwareCapabilitiesProvider accessToken={user?.access_token}>
      <PortalContext.Provider value={portalValue}>
        <RuntimeConfigBootstrap />
        <RouterProvider router={router} />
      </PortalContext.Provider>
    </TenantAwareCapabilitiesProvider>
  );
}

// Supplies the capability model with the application→tenant map it needs to resolve
// tenant-scoped roles. The applications list is already backend-scoped to what the caller
// may see, and this only mounts once signed in, so no fetch happens before authentication.
function TenantAwareCapabilitiesProvider({
  accessToken,
  children,
}: {
  accessToken: string | null | undefined;
  children: ReactNode;
}) {
  const applications = useApplications();  const appTenants = useMemo(() => {
    const map = new Map<string, string>();
    for (const app of applications.data ?? []) {
      if (app.tenantId) map.set(app.applicationId, app.tenantId);
    }
    return map;
  }, [applications.data]);
  return (
    <CapabilitiesProvider accessToken={accessToken} appTenants={appTenants}>
      {children}
    </CapabilitiesProvider>
  );
}

// Eagerly fetches server-owned runtime config (pagination, cache windows, role
// labels, AI flags) once signed in, publishing it to the runtime singleton so
// plain modules and state initializers read server values rather than defaults.
function RuntimeConfigBootstrap() {
  usePortalConfig();
  return null;
}

export default App;
