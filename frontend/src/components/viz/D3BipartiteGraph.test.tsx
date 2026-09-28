/** @vitest-environment jsdom */
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ThemeProvider } from "../../theme/ThemeProvider";
import {
  D3BipartiteGraph,
  type BipartiteLink,
  type BipartiteNode,
} from "./D3BipartiteGraph";

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

const left: BipartiteNode[] = [
  { id: "editor", label: "Editor", kind: "role", riskLevel: "MEDIUM" },
  { id: "viewer", label: "Viewer", kind: "role" },
];
const right: BipartiteNode[] = [
  { id: "doc:read", label: "doc:read", kind: "permission" },
  { id: "doc:write", label: "doc:write", kind: "permission" },
];
const links: BipartiteLink[] = [
  { source: "editor", target: "doc:read", state: "published" },
  { source: "editor", target: "doc:write", state: "draft" },
  { source: "viewer", target: "doc:read", state: "published" },
];

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

describe("D3BipartiteGraph", () => {
  it("renders one node per side and one path per link", () => {
    render(<D3BipartiteGraph left={left} right={right} links={links} />);
    expect(container.querySelectorAll("g.d3bip-node")).toHaveLength(4);
    expect(container.querySelectorAll("path.d3bip-link")).toHaveLength(3);
  });

  it("invokes onSelect when an interactive node is clicked", () => {
    const onSelect = vi.fn();
    render(
      <D3BipartiteGraph
        left={left}
        right={right}
        links={links}
        onSelect={onSelect}
      />,
    );
    const node = container.querySelector(
      'g.d3bip-node[aria-label="role: Editor"]',
    ) as SVGGElement;
    expect(node).toBeTruthy();
    act(() => {
      node.dispatchEvent(new MouseEvent("click", { bubbles: true }));
    });
    expect(onSelect).toHaveBeenCalledTimes(1);
    expect(onSelect.mock.calls[0][0]).toMatchObject({ id: "editor" });
  });

  it("drops links that reference an unknown node", () => {
    render(
      <D3BipartiteGraph
        left={left}
        right={right}
        links={[{ source: "editor", target: "missing" }]}
      />,
    );
    expect(container.querySelectorAll("path.d3bip-link")).toHaveLength(0);
  });
});
