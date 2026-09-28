/** @vitest-environment jsdom */
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ThemeProvider } from "../../theme/ThemeProvider";
import { D3Tree } from "./D3Tree";
import type { GraphNodeDatum } from "./nodes";

(
  globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }
).IS_REACT_ACT_ENVIRONMENT = true;

if (!("ResizeObserver" in globalThis)) {
  (globalThis as { ResizeObserver?: unknown }).ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
}

const tree: GraphNodeDatum = {
  id: "root:all",
  kind: "root",
  label: "All tenants",
  children: [
    {
      id: "tenant:acme",
      kind: "tenant",
      label: "Acme",
      children: [
        {
          id: "application:finance",
          kind: "application",
          label: "Finance",
          riskLevel: "MEDIUM",
        },
        {
          id: "application:hr",
          kind: "application",
          label: "HR",
          riskLevel: "LOW",
        },
      ],
    },
  ],
};

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
});

function render(node: React.ReactNode) {
  act(() => {
    root.render(<ThemeProvider>{node}</ThemeProvider>);
  });
}

describe("D3Tree", () => {
  it("renders one node per hierarchy datum and one link per edge", () => {
    render(<D3Tree data={tree} />);
    // 1 root + 1 tenant + 2 applications = 4 nodes, 3 edges.
    expect(container.querySelectorAll("g.d3tree-node")).toHaveLength(4);
    expect(container.querySelectorAll("path.d3tree-link")).toHaveLength(3);
  });

  it("invokes onSelect with the datum when an interactive node is clicked", () => {
    const onSelect = vi.fn();
    render(<D3Tree data={tree} onSelect={onSelect} />);
    const appNode = container.querySelector(
      'g.d3tree-node[aria-label="application: Finance"]',
    ) as SVGGElement;
    expect(appNode).toBeTruthy();
    act(() => {
      appNode.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });
    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect.mock.calls[0][0]).toMatchObject({
      id: "application:finance",
      kind: "application",
    });
  });

  it("marks the selected node via aria-selected", () => {
    render(
      <D3Tree data={tree} selectedId="application:hr" onSelect={() => {}} />,
    );
    const selected = container.querySelector(
      'g.d3tree-node[aria-selected="true"]',
    );
    expect(selected?.getAttribute("aria-label")).toBe("application: HR");
  });
});
