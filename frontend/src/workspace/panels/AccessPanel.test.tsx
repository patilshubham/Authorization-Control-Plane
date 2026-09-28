/** @vitest-environment jsdom */
import { act, createElement } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { RoleSummary } from "../../types";

(
  globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }
).IS_REACT_ACT_ENVIRONMENT = true;

// AccessPanel only reads specific fields off each hook's return value. Mock exactly those so the
// "Grant access" create-form can be exercised without a real QueryClient/network stack.
const roles: RoleSummary[] = [
  {
    roleKey: "content-viewer",
    name: "Content Viewer",
    privileged: false,
    riskLevel: "LOW",
    status: "ACTIVE",
    permissions: [],
  },
  {
    roleKey: "ops-admin",
    name: "Ops Admin",
    privileged: true,
    riskLevel: "HIGH",
    status: "ACTIVE",
    permissions: [],
  },
];

const createMutate = vi.fn();

vi.mock("../../api/hooks", () => ({
  useRoles: () => ({ data: roles }),
  useAssignmentsPaged: () => ({ data: { items: [], total: 0 }, isLoading: false }),
  useAssignmentsSummary: () => ({ data: { states: [], total: 0 } }),
  useAssignments: () => ({ data: [] }),
  useCreateAssignment: () => ({ mutate: createMutate, isPending: false }),
  useUpdateAssignment: () => ({ mutate: vi.fn(), isPending: false }),
  useImportAssignments: () => ({ data: undefined, mutate: vi.fn(), reset: vi.fn(), isPending: false }),
  useRevokeAssignment: () => ({ mutate: vi.fn(), isPending: false }),
  useBreakGlass: () => ({ mutate: vi.fn(), isPending: false }),
}));
vi.mock("../../capabilities", () => ({
  useCapabilities: () => ({ can: () => true }),
}));
vi.mock("../../components/Toast", () => ({
  useToast: () => ({ success: vi.fn(), error: vi.fn() }),
}));
vi.mock("../../components/charts", () => ({
  DonutChart: () => null,
  useChartTheme: () => ({}),
}));
vi.mock("../../components/viz/VizModal", () => ({
  VizModal: () => null,
}));
vi.mock("../../components/viz/D3BipartiteGraph", () => ({
  D3BipartiteGraph: () => null,
}));

import { AccessPanel } from "./AccessPanel";

let container: HTMLDivElement;
let root: Root;

beforeEach(() => {
  container = document.createElement("div");
  document.body.appendChild(container);
  root = createRoot(container);
});

afterEach(() => {
  act(() => root.unmount());
  container.remove();
  createMutate.mockClear();
});

function click(el: Element) {
  act(() => {
    el.dispatchEvent(new MouseEvent("click", { bubbles: true }));
  });
}

// Fire a change event the way React's synthetic system expects (native setter + event) —
// directly assigning .value and dispatching a plain event is swallowed by React's tracker.
function setInputValue(input: HTMLInputElement, value: string) {
  const setter = Object.getOwnPropertyDescriptor(
    window.HTMLInputElement.prototype,
    "value",
  )?.set;
  setter?.call(input, value);
  act(() => {
    input.dispatchEvent(new Event("input", { bubbles: true }));
  });
}

function setSelectValue(select: HTMLSelectElement, value: string) {
  const setter = Object.getOwnPropertyDescriptor(
    window.HTMLSelectElement.prototype,
    "value",
  )?.set;
  setter?.call(select, value);
  act(() => {
    select.dispatchEvent(new Event("change", { bubbles: true }));
  });
}

function openGrantAccess() {
  const buttons = Array.from(container.querySelectorAll("button"));
  const grantButton = buttons.find((b) => b.textContent === "Grant access");
  expect(grantButton).toBeTruthy();
  click(grantButton!);
}

function selectRole(roleKey: string) {
  // Scope to the open "Grant access" dialog — the main table's state-filter is also a <select>.
  const dialog = container.querySelector('[role="dialog"]') as HTMLElement;
  const select = dialog.querySelector("select") as HTMLSelectElement;
  expect(select).toBeTruthy();
  setSelectValue(select, roleKey);
}

function getPrivilegedCheckbox() {
  return container.querySelector('input[type="checkbox"]') as HTMLInputElement;
}

function getGrantSubmitButton() {
  const buttons = Array.from(container.querySelectorAll("button"));
  return buttons.find((b) => b.textContent === "Grant") as HTMLButtonElement;
}

describe("AccessPanel — Grant access privileged-role rules", () => {
  it("auto-checks and disables the 'Privileged assignment' checkbox for a privileged role", async () => {
    await act(async () => {
      root.render(createElement(AccessPanel, { appId: "app-1" }));
    });

    openGrantAccess();
    selectRole("ops-admin");

    const checkbox = getPrivilegedCheckbox();
    expect(checkbox.checked).toBe(true);
    expect(checkbox.disabled).toBe(true);
  });

  it("leaves the checkbox unchecked and disabled for a non-privileged role", async () => {
    await act(async () => {
      root.render(createElement(AccessPanel, { appId: "app-1" }));
    });

    openGrantAccess();
    selectRole("content-viewer");

    const checkbox = getPrivilegedCheckbox();
    expect(checkbox.checked).toBe(false);
    expect(checkbox.disabled).toBe(true);
  });

  it("disables Grant until an expiry is set for a privileged role, then enables it", async () => {
    await act(async () => {
      root.render(createElement(AccessPanel, { appId: "app-1" }));
    });

    openGrantAccess();

    const subjectEmailInput = container.querySelector(
      'input[placeholder="user@company.com"]',
    ) as HTMLInputElement;
    setInputValue(subjectEmailInput, "someone@company.com");

    selectRole("ops-admin");

    // Subject + role are set, but the privileged role still has no expiry — Grant must stay disabled.
    expect(getGrantSubmitButton().disabled).toBe(true);

    const dateInput = container.querySelector('input[type="date"]') as HTMLInputElement;
    setInputValue(dateInput, "2999-01-01");

    expect(getGrantSubmitButton().disabled).toBe(false);

    click(getGrantSubmitButton());

    expect(createMutate).toHaveBeenCalledTimes(1);
    const [payload] = createMutate.mock.calls[0] as [{ roleKey: string; validUntil: string | null }];
    expect(payload.roleKey).toBe("ops-admin");
    expect(payload.validUntil).not.toBeNull();
    // The now-removed client-trust field must never be sent — the server derives privilege itself.
    expect(payload).not.toHaveProperty("privileged");
  });

  it("allows Grant for a non-privileged role without setting an expiry", async () => {
    await act(async () => {
      root.render(createElement(AccessPanel, { appId: "app-1" }));
    });

    openGrantAccess();

    const subjectEmailInput = container.querySelector(
      'input[placeholder="user@company.com"]',
    ) as HTMLInputElement;
    setInputValue(subjectEmailInput, "someone@company.com");

    selectRole("content-viewer");

    expect(getGrantSubmitButton().disabled).toBe(false);
  });
});
