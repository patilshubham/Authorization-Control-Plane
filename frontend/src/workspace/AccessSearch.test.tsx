/** @vitest-environment jsdom */
import { act, createElement } from "react";
import { createRoot, type Root } from "react-dom/client";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The component only reads { mutateAsync, isPending } from the search hook and a boolean from the
// feature flag, so mock exactly those. mutateAsync ignores its argument and returns our canned tree.
const mutateAsync = vi.fn();
vi.mock("../api/hooks", () => ({
  usePlatformAccessSearch: () => ({ mutateAsync, isPending: false }),
}));
vi.mock("../api/aiConfig", () => ({
  useAiFeature: () => true,
}));

import { AccessSearch } from "./AccessSearch";

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
  vi.clearAllMocks();
});

describe("AccessSearch tree rendering", () => {
  it("renders a nested second level (grandchildren) for a two-level include", async () => {
    // role → permissions → policies: the policy is a grandchild of the role, nested under its
    // permission. Proves the depth-2 chain (ASKAI-C2) renders, not just the first level.
    mutateAsync.mockResolvedValue({
      entity: "role",
      explanation: null,
      filters: [],
      mode: "tree",
      results: [
        {
          applicationId: "app-1",
          entityType: "ROLE",
          title: "pricing-admin",
          detail: "Role",
          deepLinkKind: "role",
          deepLinkKey: "pricing-admin",
          children: [
            {
              applicationId: "app-1",
              entityType: "PERMISSION",
              title: "price.publish",
              detail: "price:publish",
              deepLinkKind: "permission",
              deepLinkKey: "price.publish",
              children: [
                {
                  applicationId: "app-1",
                  entityType: "POLICY",
                  title: "allow-publish",
                  detail: "ALLOW",
                  deepLinkKind: "policy",
                  deepLinkKey: "allow-publish",
                  children: null,
                },
              ],
            },
          ],
        },
      ],
    });

    await act(async () => {
      root.render(createElement(MemoryRouter, null, createElement(AccessSearch)));
    });

    // Trigger a search via the first starter suggestion; the mocked hook returns the tree regardless.
    const chip = container.querySelector<HTMLButtonElement>(".access-search-chip");
    expect(chip).not.toBeNull();
    await act(async () => {
      chip!.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });

    // The grandchild policy must be nested inside a grandchildren list, under the permission child.
    const grandchildren = container.querySelector(".access-search-grandchildren");
    expect(grandchildren).not.toBeNull();
    expect(grandchildren!.textContent).toContain("allow-publish");

    // All three levels are present in the rendered tree.
    const text = container.textContent ?? "";
    expect(text).toContain("pricing-admin");
    expect(text).toContain("price.publish");
    expect(text).toContain("allow-publish");
  });

  it("renders a nested third level for a three-level include (tenant -> role -> permission -> policy)", async () => {
    // tenant → roles → permissions → policies: the policy is a great-grandchild of the tenant,
    // nested three levels deep. Proves ChildBranch recurses beyond the depth-2 chain.
    mutateAsync.mockResolvedValue({
      entity: "tenant",
      explanation: null,
      filters: [],
      mode: "tree",
      results: [
        {
          applicationId: "app-1",
          entityType: "TENANT",
          title: "Acme Corp",
          detail: "Tenant",
          deepLinkKind: "tenant",
          deepLinkKey: "acme",
          children: [
            {
              applicationId: "app-1",
              entityType: "ROLE",
              title: "pricing-admin",
              detail: "Role",
              deepLinkKind: "role",
              deepLinkKey: "pricing-admin",
              children: [
                {
                  applicationId: "app-1",
                  entityType: "PERMISSION",
                  title: "price.publish",
                  detail: "price:publish",
                  deepLinkKind: "permission",
                  deepLinkKey: "price.publish",
                  children: [
                    {
                      applicationId: "app-1",
                      entityType: "POLICY",
                      title: "allow-publish",
                      detail: "ALLOW",
                      deepLinkKind: "policy",
                      deepLinkKey: "allow-publish",
                      children: null,
                    },
                  ],
                },
              ],
            },
          ],
        },
      ],
    });

    await act(async () => {
      root.render(createElement(MemoryRouter, null, createElement(AccessSearch)));
    });

    const chip = container.querySelector<HTMLButtonElement>(".access-search-chip");
    expect(chip).not.toBeNull();
    await act(async () => {
      chip!.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });

    // All four levels are present in the rendered tree.
    const text = container.textContent ?? "";
    expect(text).toContain("Acme Corp");
    expect(text).toContain("pricing-admin");
    expect(text).toContain("price.publish");
    expect(text).toContain("allow-publish");
  });

  it("shows an interpretation header with entity, filter chips, and a result count", async () => {
    mutateAsync.mockResolvedValue({
      entity: "role",
      explanation: "Roles that are privileged.",
      filters: [{ field: "privileged", operator: "eq", value: "true" }],
      mode: "records",
      results: [
        {
          applicationId: "app-1",
          entityType: "ROLE",
          title: "admin",
          detail: "Privileged role",
          deepLinkKind: "role",
          deepLinkKey: "admin",
          children: null,
        },
      ],
    });

    await act(async () => {
      root.render(createElement(MemoryRouter, null, createElement(AccessSearch)));
    });
    const chip = container.querySelector<HTMLButtonElement>(".access-search-chip");
    await act(async () => {
      chip!.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });

    // Interpretation, resolved entity badge, humanized filter chip, and result count are all shown.
    const summary = container.querySelector(".access-search-summary");
    expect(summary).not.toBeNull();
    const text = summary!.textContent ?? "";
    expect(text).toContain("Roles that are privileged.");
    expect(text).toContain("Role");
    expect(text).toContain("Privileged");
    expect(text).toContain("true");
    expect(text).toContain("1 result");
  });

  it("renders a nested two-level group (cross-tab) with sub-groups and proportion bars", async () => {
    mutateAsync.mockResolvedValue({
      entity: "role",
      explanation: null,
      filters: [],
      mode: "group",
      results: [
        {
          applicationId: "",
          entityType: "ROLE",
          title: "HIGH",
          detail: "3 roles",
          deepLinkKind: null,
          deepLinkKey: null,
          children: [
            {
              applicationId: "",
              entityType: "ROLE",
              title: "ACTIVE",
              detail: "2 roles",
              deepLinkKind: null,
              deepLinkKey: null,
              children: null,
            },
            {
              applicationId: "",
              entityType: "ROLE",
              title: "DISABLED",
              detail: "1 role",
              deepLinkKind: null,
              deepLinkKey: null,
              children: null,
            },
          ],
        },
      ],
    });

    await act(async () => {
      root.render(createElement(MemoryRouter, null, createElement(AccessSearch)));
    });
    const chip = container.querySelector<HTMLButtonElement>(".access-search-chip");
    await act(async () => {
      chip!.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });

    const subgroups = container.querySelector(".access-search-subgroups");
    expect(subgroups).not.toBeNull();
    expect(subgroups!.textContent).toContain("ACTIVE");
    expect(subgroups!.textContent).toContain("DISABLED");
    // Proportion bars are drawn for the buckets.
    expect(container.querySelector(".access-search-proportion-fill")).not.toBeNull();
  });
});
