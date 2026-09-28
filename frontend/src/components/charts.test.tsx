/** @vitest-environment jsdom */
import { act } from "react";
import { createRoot, type Root } from "react-dom/client";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { ThemeProvider } from "../theme/ThemeProvider";
import {
  AreaTrend,
  BarDistribution,
  DonutChart,
  buildActivityTrend,
  buildUsageTrend,
  type DistributionDatum,
  type DonutDatum,
} from "./charts";

(
  globalThis as { IS_REACT_ACT_ENVIRONMENT?: boolean }
).IS_REACT_ACT_ENVIRONMENT = true;

// jsdom lacks ResizeObserver; useResizeWidth falls back to its initial width, so
// the width-gated charts still render. Provide a no-op stub for completeness.
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

describe("AreaTrend", () => {
  it("renders an area and line path for the series", () => {
    const data = [
      { label: "Mon", count: 3 },
      { label: "Tue", count: 5 },
      { label: "Wed", count: 2 },
    ];
    render(<AreaTrend data={data} />);
    expect(container.querySelector("svg")).toBeTruthy();
    expect(container.querySelector("path.d3-area")).toBeTruthy();
    expect(container.querySelector("path.d3-line")).toBeTruthy();
  });
});

describe("BarDistribution", () => {
  it("renders one bar per datum", () => {
    const data: DistributionDatum[] = [
      { label: "Low", value: 4 },
      { label: "High", value: 1 },
      { label: "Critical", value: 2 },
    ];
    render(<BarDistribution data={data} />);
    expect(container.querySelectorAll("path.d3-bar")).toHaveLength(3);
  });

  it("keeps short, sparse labels horizontal", () => {
    const data: DistributionDatum[] = [
      { label: "Low", value: 4 },
      { label: "High", value: 1 },
    ];
    render(<BarDistribution data={data} />);
    const rotated = Array.from(container.querySelectorAll("svg text")).filter(
      (el) => (el.getAttribute("transform") ?? "").includes("rotate"),
    );
    expect(rotated).toHaveLength(0);
  });

  it("slants labels when many long labels would overlap", () => {
    const data: DistributionDatum[] = Array.from({ length: 12 }, (_, i) => ({
      label: `Very long feature label ${i}`,
      value: i + 1,
    }));
    render(<BarDistribution data={data} />);
    const rotated = Array.from(container.querySelectorAll("svg text")).filter(
      (el) => (el.getAttribute("transform") ?? "").includes("rotate"),
    );
    expect(rotated.length).toBe(12);
  });
});

describe("DonutChart", () => {
  it("renders one arc per datum", () => {
    const data: DonutDatum[] = [
      { label: "Allow", value: 6, color: "#1f8a4c" },
      { label: "Deny", value: 2, color: "#d64545" },
    ];
    render(<DonutChart data={data} centerLabel="8" centerSub="Policies" />);
    expect(container.querySelectorAll("path.d3-arc")).toHaveLength(2);
    expect(container.textContent).toContain("8");
    expect(container.textContent).toContain("Policies");
  });

  it("renders a single placeholder ring when the total is zero", () => {
    render(<DonutChart data={[]} />);
    expect(container.querySelectorAll("path.d3-arc")).toHaveLength(1);
  });
});

describe("buildActivityTrend", () => {
  it("produces one bucket per day and counts events in-window", () => {
    // Anchor the event to local midnight of today so its UTC date key matches the
    // last bucket the builder emits, independent of the runner's timezone.
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const trend = buildActivityTrend([{ timestamp: today.toISOString() }], 7);
    expect(trend).toHaveLength(7);
    expect(trend[trend.length - 1].count).toBe(1);
    expect(trend.reduce((sum, d) => sum + d.count, 0)).toBe(1);
  });

  it("counts a same-day event in the last bucket regardless of time of day", () => {
    // A mid-afternoon event on the viewer's current day must land in today's
    // (final) bucket. The previous implementation keyed buckets by the UTC date
    // of local midnight, dropping such events for viewers east of UTC.
    const noonToday = new Date();
    noonToday.setHours(12, 0, 0, 0);
    const trend = buildActivityTrend([{ timestamp: noonToday.toISOString() }], 14);
    expect(trend).toHaveLength(14);
    expect(trend[trend.length - 1].count).toBe(1);
    expect(trend.reduce((sum, d) => sum + d.count, 0)).toBe(1);
  });

  it("ignores events outside the window", () => {
    const old = new Date();
    old.setDate(old.getDate() - 30);
    const trend = buildActivityTrend([{ timestamp: old.toISOString() }], 7);
    expect(trend.reduce((sum, d) => sum + d.count, 0)).toBe(0);
  });
});

describe("buildUsageTrend", () => {
  it("zero-fills the window and always ends on today (UTC)", () => {
    const trend = buildUsageTrend([], 30);
    expect(trend).toHaveLength(30);
    expect(trend.every((d) => d.count === 0)).toBe(true);

    const now = new Date();
    const todayLabel = new Date(
      Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()),
    ).toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
      timeZone: "UTC",
    });
    expect(trend[trend.length - 1].label).toBe(todayLabel);
  });

  it("places today's backend count on the final bucket", () => {
    const now = new Date();
    const todayKey = new Date(
      Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()),
    )
      .toISOString()
      .slice(0, 10);
    const trend = buildUsageTrend(
      [{ date: `${todayKey}T00:00:00+00:00`, count: 5 }],
      14,
    );
    expect(trend).toHaveLength(14);
    expect(trend[trend.length - 1].count).toBe(5);
    expect(trend.reduce((sum, d) => sum + d.count, 0)).toBe(5);
  });
});
