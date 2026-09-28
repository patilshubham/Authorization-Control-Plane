// D3Heatmap — a GitHub-style calendar heatmap of daily event volume.
//
// d3-scale/d3-array compute the day buckets and intensity thresholds; React
// renders the grid of day cells as plain SVG (no getBBox), so it stays theme
// aware and testable in jsdom. Used by the audit and activity views.

import { useMemo, useState } from "react";
import { max as d3max } from "d3-array";
import { scaleThreshold } from "d3-scale";
import { useResizeWidth } from "../useResizeWidth";

const GAP = 3;
const CELL_MIN = 12; // keep cells legible on narrow layouts
const CELL_MAX = 40; // cap growth so the grid stays calendar-like, not blocky
const TOP = 18; // month labels
const LEFT = 34; // weekday labels
const WEEKDAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
const MONTHS = [
  "Jan",
  "Feb",
  "Mar",
  "Apr",
  "May",
  "Jun",
  "Jul",
  "Aug",
  "Sep",
  "Oct",
  "Nov",
  "Dec",
];

function dayKey(d: Date): string {
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(
    d.getDate(),
  ).padStart(2, "0")}`;
}

/** Bucket event timestamps into per-day counts keyed by local yyyy-mm-dd. */
export function buildDayCounts(
  events: Array<{ timestamp: string }>,
): Map<string, number> {
  const map = new Map<string, number>();
  for (const e of events) {
    const d = new Date(e.timestamp);
    if (Number.isNaN(d.getTime())) continue;
    const key = dayKey(d);
    map.set(key, (map.get(key) ?? 0) + 1);
  }
  return map;
}

export function D3Heatmap({
  events,
  dayCounts,
  weeks = 16,
  ariaLabel = "Activity heatmap",
}: {
  events?: Array<{ timestamp: string }>;
  /** Pre-aggregated per-day counts (yyyy-mm-dd → count). When provided, it is
   * used directly instead of bucketing `events`, so the feed can paginate while
   * the heatmap still reflects the full window via a lightweight summary. */
  dayCounts?: Array<{ date: string; count: number }>;
  weeks?: number;
  ariaLabel?: string;
}) {
  const [hovered, setHovered] = useState<{
    date: string;
    count: number;
    label: string;
  } | null>(null);
  const [wrapRef, wrapWidth] = useResizeWidth<HTMLDivElement>(320);

  const model = useMemo(() => {
    const counts = dayCounts
      ? new Map(dayCounts.map((d) => [d.date, d.count]))
      : buildDayCounts(events ?? []);
    // End on today; start `weeks` columns back, aligned to the week's Sunday.
    const today = new Date();
    today.setHours(0, 0, 0, 0);
    const end = today;
    const start = new Date(end);
    start.setDate(start.getDate() - (weeks * 7 - 1));
    start.setDate(start.getDate() - start.getDay()); // back to Sunday

    const cells: Array<{
      key: string;
      col: number;
      row: number;
      count: number;
      label: string;
    }> = [];
    const monthTicks: Array<{ col: number; label: string }> = [];
    let seenMonth = -1;

    const cursor = new Date(start);
    let col = 0;
    while (cursor <= end) {
      const row = cursor.getDay();
      if (row === 0)
        col = Math.round(
          (cursor.getTime() - start.getTime()) / (7 * 86_400_000),
        );
      const key = dayKey(cursor);
      const count = counts.get(key) ?? 0;
      const label = `${MONTHS[cursor.getMonth()]} ${cursor.getDate()}`;
      cells.push({ key, col, row, count, label });
      if (cursor.getMonth() !== seenMonth && cursor.getDate() <= 7) {
        seenMonth = cursor.getMonth();
        monthTicks.push({ col, label: MONTHS[cursor.getMonth()] });
      }
      cursor.setDate(cursor.getDate() + 1);
    }

    const totalCols = Math.max(...cells.map((c) => c.col), 0) + 1;
    const maxCount = d3max(cells, (c) => c.count) ?? 0;
    return { cells, monthTicks, totalCols, maxCount };
  }, [events, dayCounts, weeks]);

  // Four intensity buckets over [1, max]; 0 stays "empty". Colors come from
  // live CSS variables so cells re-tint instantly when the theme flips.
  const color = useMemo(() => {
    const m = Math.max(model.maxCount, 1);
    const scale = scaleThreshold<number, number>()
      .domain([m * 0.25, m * 0.5, m * 0.75])
      .range([0.3, 0.5, 0.72, 1]);
    return (count: number): { fill: string; opacity: number } =>
      count <= 0
        ? { fill: "var(--surface-2)", opacity: 1 }
        : { fill: "var(--accent)", opacity: scale(count) };
  }, [model.maxCount]);

  // Size the day cells to fill the measured container width so the calendar
  // grows with its panel, clamped so it stays a legible calendar. Rendered at
  // real pixels (no viewBox up-scaling) so month/weekday labels stay crisp.
  const step = useMemo(() => {
    const avail = wrapWidth - LEFT - 4;
    const raw = avail / model.totalCols;
    // Floor keeps the rendered grid from spilling a sub-pixel past the
    // container (which would trigger a stray horizontal scrollbar).
    return Math.floor(Math.min(Math.max(raw, CELL_MIN + GAP), CELL_MAX + GAP));
  }, [wrapWidth, model.totalCols]);
  const cell = step - GAP;

  const width = LEFT + model.totalCols * step + 4;
  const heightPx = TOP + 7 * step + 4;

  const totalEvents = dayCounts
    ? dayCounts.reduce((sum, d) => sum + d.count, 0)
    : (events?.length ?? 0);

  return (
    <div className="d3heatmap" ref={wrapRef}>
      <div className="d3heatmap-caption muted" aria-live="polite">
        {hovered
          ? `${hovered.label ?? hovered.date}: ${hovered.count} event${hovered.count === 1 ? "" : "s"}`
          : `${totalEvents} event${totalEvents === 1 ? "" : "s"} · last ${weeks} weeks`}
      </div>
      <svg
        className="d3heatmap-svg"
        width={width}
        height={heightPx}
        viewBox={`0 0 ${width} ${heightPx}`}
        preserveAspectRatio="xMinYMin meet"
        role="img"
        aria-label={ariaLabel}
      >
        {model.monthTicks.map((mt, i) => (
          <text
            key={`${mt.label}-${i}`}
            className="d3heatmap-month"
            x={LEFT + mt.col * step}
            y={TOP - 6}
          >
            {mt.label}
          </text>
        ))}
        {WEEKDAYS.map((wd, row) => (
          <text
            key={wd}
            className="d3heatmap-weekday"
            x={LEFT - 6}
            y={TOP + row * step + cell / 2 + 3}
            textAnchor="end"
          >
            {wd}
          </text>
        ))}
        {model.cells.map((c) => {
          const { fill, opacity } = color(c.count);
          return (
            <rect
              key={c.key}
              className="d3heatmap-cell"
              x={LEFT + c.col * step}
              y={TOP + c.row * step}
              width={cell}
              height={cell}
              rx={3}
              style={{ fill, fillOpacity: opacity }}
              onMouseEnter={() =>
                setHovered({ date: c.key, count: c.count, label: c.label })
              }
              onMouseLeave={() => setHovered(null)}
            >
              <title>{`${c.label}: ${c.count} event${c.count === 1 ? "" : "s"}`}</title>
            </rect>
          );
        })}
      </svg>
    </div>
  );
}
