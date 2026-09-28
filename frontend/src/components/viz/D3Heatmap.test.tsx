/** @vitest-environment jsdom */
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { ThemeProvider } from "../../theme/ThemeProvider";
import { D3Heatmap, buildDayCounts } from "./D3Heatmap";

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

describe("buildDayCounts", () => {
  it("buckets events by local day and ignores invalid timestamps", () => {
    const now = new Date();
    const iso = now.toISOString();
    const counts = buildDayCounts([
      { timestamp: iso },
      { timestamp: iso },
      { timestamp: "not-a-date" },
    ]);
    const total = [...counts.values()].reduce((a, b) => a + b, 0);
    expect(total).toBe(2);
  });
});

describe("D3Heatmap", () => {
  it("renders a grid of day cells and a caption", () => {
    const today = new Date().toISOString();
    render(<D3Heatmap events={[{ timestamp: today }]} weeks={4} />);
    const cells = container.querySelectorAll("rect.d3heatmap-cell");
    // 4 weeks back aligned to Sunday spans roughly a month of days.
    expect(cells.length).toBeGreaterThan(20);
    expect(container.querySelector(".d3heatmap-caption")).toBeTruthy();
  });

  it("renders cells even with no events", () => {
    render(<D3Heatmap events={[]} weeks={4} />);
    expect(
      container.querySelectorAll("rect.d3heatmap-cell").length,
    ).toBeGreaterThan(0);
  });
});
