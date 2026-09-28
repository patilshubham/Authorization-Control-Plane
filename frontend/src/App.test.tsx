/** @vitest-environment jsdom */
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Build a valid JWT (header.payload.signature) whose payload carries the platform
// super-admin claim, so the capability model grants full write access in tests.
function encodeJwt(payload: Record<string, unknown>): string {
  const b64url = (obj: Record<string, unknown>) =>
    btoa(JSON.stringify(obj))
      .replace(/\+/g, "-")
      .replace(/\//g, "_")
      .replace(/=+$/, "");
  return `${b64url({ alg: "none", typ: "JWT" })}.${b64url(payload)}.`;
}

const adminAccessToken = encodeJwt({
  email: "admin@local.test",
  acp_platform_role: ["PlatformSuperAdmin"],
});

// Mock the OIDC auth module so the protected shell can render without a live IdP.
const signedInUser = {
  expired: false,
  access_token: adminAccessToken,
  profile: { email: "admin@local.test" },
};

vi.mock("./auth", () => ({
  getUser: vi.fn(async () => signedInUser),
  handleRedirectCallback: vi.fn(async () => null),
  getAccessToken: vi.fn(async () => "test-token"),
  trySilentSignin: vi.fn(async () => null),
  completeSilentRenewIfIframe: vi.fn(async () => false),
  login: vi.fn(async () => {}),
  logout: vi.fn(async () => {}),
  getDisplayName: (user: { profile?: { email?: string } } | null) =>
    user?.profile?.email ?? "Signed out",
}));

// Mock the API surface so React Query hooks resolve with deterministic data.
const testApp = {
  applicationId: "finance-app",
  name: "Finance",
  riskLevel: "MEDIUM",
  status: "ACTIVE",
  tenantId: "acme",
};

vi.mock("./apiClient", () => ({
  userFacingError: (e: unknown) => (e instanceof Error ? e.message : String(e)),
  portalApi: {
    listTenants: vi.fn(async () => []),
    listApplications: vi.fn(async () => [testApp]),
    listRoles: vi.fn(async () => []),
    listPermissions: vi.fn(async () => []),
    listRoleMappings: vi.fn(async () => []),
    listPolicies: vi.fn(async () => []),
    listAssignments: vi.fn(async () => []),
    listOidcProviders: vi.fn(async () => []),
    listAuditEvents: vi.fn(async () => []),
    listGlobalAuditEvents: vi.fn(async () => []),
    listAuditEventsPaged: vi.fn(async () => ({
      items: [],
      page: 1,
      pageSize: 25,
      total: 0,
    })),
    getAuditActivitySummary: vi.fn(async () => ({ total: 0, days: [] })),
    listUsers: vi.fn(async () => []),
    getUser: vi.fn(async () => ({ email: "", assignments: [] })),
    getTenantDetail: vi.fn(async () => ({
      tenant: {},
      applications: [],
      rollup: {},
    })),
    getApplicationOverview: vi.fn(async () => ({})),
    getPlatformOverview: vi.fn(async () => ({
      tenantCount: 0,
      applicationCount: 1,
      roleCount: 0,
      permissionCount: 0,
      policyCount: 0,
      assignmentCount: 0,
      activeAssignmentCount: 0,
      applicationsByRisk: {},
      applicationsByTenant: [],
      recentAudit: [],
    })),
    createApplication: vi.fn(async () => testApp),
    simulate: vi.fn(async () => ({
      allowed: true,
      decisionId: "d1",
      denyReason: "",
    })),
  },
}));

import App from "./App";
import { router } from "./router";
import * as auth from "./auth";

(
  globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }
).IS_REACT_ACT_ENVIRONMENT = true;

// The D3 charts measure their container via ResizeObserver (see useResizeWidth),
// which jsdom does not implement. Provide a no-op stub so chart-bearing pages
// render in tests.
if (!("ResizeObserver" in globalThis)) {
  (globalThis as { ResizeObserver?: unknown }).ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
}

let container: HTMLDivElement;
let root: Root;

async function renderApp() {
  // The data router is a module singleton that retains its location across
  // tests; reset it to root so each test starts in the platform area.
  await act(async () => {
    await router.navigate("/");
  });
  await act(async () => {
    root = createRoot(container);
    root.render(<App />);
  });
  // Flush auth resolution + initial query settling. The index landing waits
  // for the applications query before deciding where to route, so allow enough
  // microtask/macrotask cycles for React Query to resolve the mocked fetches.
  for (let i = 0; i < 8; i++) {
    await act(async () => {
      await Promise.resolve();
    });
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
  }
}

beforeEach(() => {
  vi.clearAllMocks();
  vi.mocked(auth.getUser).mockResolvedValue(signedInUser as never);
  vi.mocked(auth.handleRedirectCallback).mockResolvedValue(null);
  // The browser router shares window.history across tests; reset to root so each
  // test starts in the platform area rather than a route left by a prior test.
  window.history.pushState({}, "", "/");
  container = document.createElement("div");
  document.body.appendChild(container);
});

afterEach(() => {
  act(() => root.unmount());
  container.remove();
});

function clickByText(text: string) {
  const element = Array.from(
    container.querySelectorAll<HTMLElement>("button, [role='option']"),
  ).find((el) => el.textContent?.includes(text));
  expect(element).toBeTruthy();
  act(() => element?.click());
}

describe("Console", () => {
  it("renders the platform shell with the command palette trigger and platform scope", async () => {
    await renderApp();

    expect(container.textContent).toContain("Authorization Control Plane");
    expect(container.textContent).toContain("Search or jump to");
    // The platform area deliberately has NO application selector — scope is global.
    expect(container.querySelector("#application-selector")).toBeNull();
    expect(container.textContent).toContain("Platform");
    expect(container.textContent).toContain("admin@local.test");
  });

  it("shows the OIDC sign-in screen when no user session exists", async () => {
    vi.mocked(auth.getUser).mockResolvedValue(null as never);

    await renderApp();

    expect(container.textContent).toContain("Govern access with local OIDC");
    expect(container.textContent).toContain("Sign in with Keycloak");

    clickByText("Sign in with Keycloak");
    expect(auth.login).toHaveBeenCalledTimes(1);
  });

  it("invokes logout from the account menu", async () => {
    await renderApp();

    // The account chip now opens a menu; logout is an explicit item within it.
    clickByText("admin@local.test");
    clickByText("Sign out");
    expect(auth.logout).toHaveBeenCalledTimes(1);
  });

  it("opens the command palette with a keyboard shortcut", async () => {
    await renderApp();

    await act(async () => {
      window.dispatchEvent(
        new KeyboardEvent("keydown", { key: "k", ctrlKey: true }),
      );
    });

    const palette = container.querySelector(
      "[role='dialog'][aria-label='Command palette']",
    );
    expect(palette).toBeTruthy();
    const input = palette?.querySelector<HTMLInputElement>("input");
    expect(input?.getAttribute("placeholder")).toContain("run a command");
  });

  it("navigates into an application workspace and to its simulator via the palette", async () => {
    await renderApp();

    // Enter the application workspace from the platform command palette.
    await act(async () => {
      window.dispatchEvent(
        new KeyboardEvent("keydown", { key: "k", ctrlKey: true }),
      );
    });
    clickByText("Finance");
    for (let i = 0; i < 4; i++) {
      await act(async () => {
        await Promise.resolve();
      });
    }

    // The application scope is now visible; jump to the simulator.
    await act(async () => {
      window.dispatchEvent(
        new KeyboardEvent("keydown", { key: "k", ctrlKey: true }),
      );
    });
    clickByText("Simulator");
    for (let i = 0; i < 3; i++) {
      await act(async () => {
        await Promise.resolve();
      });
    }

    expect(container.textContent).toContain("Authorization simulator");
    expect(container.querySelector("main")).toBeTruthy();
  });

  it("creates an application from the command palette", async () => {
    await renderApp();

    await act(async () => {
      window.dispatchEvent(
        new KeyboardEvent("keydown", { key: "k", ctrlKey: true }),
      );
    });
    clickByText("New application");

    expect(
      container.querySelector("[role='dialog'][aria-modal='true']"),
    ).toBeTruthy();
    expect(container.textContent).toContain("New application");
  });

  it("provides accessible names for all controls", async () => {
    await renderApp();

    // Platform shell has no application selector; verify every control is named.
    const buttons = Array.from(container.querySelectorAll("button"));
    expect(
      buttons.every((button) =>
        Boolean(
          button.textContent?.trim() || button.getAttribute("aria-label"),
        ),
      ),
    ).toBe(true);
  });
});
