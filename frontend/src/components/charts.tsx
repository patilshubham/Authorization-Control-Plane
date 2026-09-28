import { useMemo, useState } from "react";
import { scaleBand, scaleLinear, scalePoint } from "d3-scale";
import {
  arc as d3arc,
  area as d3area,
  curveMonotoneX,
  line as d3line,
  pie as d3pie,
} from "d3-shape";
import type { PieArcDatum } from "d3-shape";
import { max as d3max } from "d3-array";
import { useTheme } from "../theme/ThemeProvider";
import { useResizeWidth } from "./useResizeWidth";

/**
 * Resolve the live design-token palette into concrete color strings that the
 * SVG-based charting library can consume. Re-reads whenever the theme flips so
 * charts stay in sync with light/dark mode.
 */
export function useChartTheme() {
  const { theme } = useTheme();
  return useMemo(() => {
    const css = getComputedStyle(document.documentElement);
    const read = (name: string, fallback: string) => {
      const v = css.getPropertyValue(name).trim();
      return v || fallback;
    };
    return {
      theme,
      accent: read("--accent", "#1f6f5c"),
      accentText: read("--accent-text", "#1f6f5c"),
      success: read("--success", "#1f8a4c"),
      danger: read("--danger", "#d64545"),
      warning: read("--warning", "#b7791f"),
      info: read("--info", "#2563a8"),
      muted: read("--muted", "#6b747d"),
      text: read("--text", "#3b434c"),
      textH: read("--text-h", "#14181c"),
      border: read("--border", "#e6e9ed"),
      surface: read("--surface", "#ffffff"),
      surface2: read("--surface-2", "#f3f5f7"),
      grid: read("--border", "#e6e9ed"),
      risk: {
        LOW: read("--risk-low", "#3f9d6b"),
        MEDIUM: read("--risk-medium", "#c99a2e"),
        HIGH: read("--risk-high", "#d97706"),
        CRITICAL: read("--risk-critical", "#c0392b"),
      },
    };
  }, [theme]);
}

type ChartTheme = ReturnType<typeof useChartTheme>;

type TooltipDatum = { name?: string; value?: number | string; color?: string };

function ChartTooltip({
  active,
  payload,
  label,
  surface,
  border,
  textH,
  muted,
}: {
  active?: boolean;
  payload?: Array<TooltipDatum & { payload?: Record<string, unknown> }>;
  label?: string | number;
  surface: string;
  border: string;
  textH: string;
  muted: string;
}) {
  if (!active || !payload || payload.length === 0) return null;
  return (
    <div
      style={{
        background: surface,
        border: `1px solid ${border}`,
        borderRadius: 10,
        padding: "8px 10px",
        boxShadow: "0 6px 20px rgba(0,0,0,0.12)",
        fontSize: 12,
        color: textH,
        minWidth: 96,
      }}
    >
      {label !== undefined && label !== "" && (
        <div style={{ color: muted, marginBottom: 4, fontWeight: 600 }}>
          {label}
        </div>
      )}
      {payload.map((p, i) => (
        <div key={i} style={{ display: "flex", alignItems: "center", gap: 8 }}>
          <span
            aria-hidden="true"
            style={{
              width: 9,
              height: 9,
              borderRadius: 3,
              background: p.color ?? textH,
              display: "inline-block",
            }}
          />
          <span style={{ flex: 1 }}>{p.name}</span>
          <strong style={{ marginLeft: 8 }}>{p.value}</strong>
        </div>
      ))}
    </div>
  );
}

/** Floating tooltip anchored to a pointer position within a chart wrapper. */
function FloatingTooltip({
  x,
  y,
  width,
  t,
  label,
  payload,
}: {
  x: number;
  y: number;
  width: number;
  t: ChartTheme;
  label?: string | number;
  payload: Array<TooltipDatum>;
}) {
  // Flip the tooltip to the left of the cursor when near the right edge.
  const flip = x > width - 120;
  return (
    <div
      style={{
        position: "absolute",
        left: x,
        top: y,
        transform: `translate(${flip ? "-100%" : "0"}, -50%) translateX(${flip ? -10 : 10}px)`,
        pointerEvents: "none",
        zIndex: 2,
      }}
    >
      <ChartTooltip
        active
        payload={payload}
        label={label}
        surface={t.surface}
        border={t.border}
        textH={t.textH}
        muted={t.muted}
      />
    </div>
  );
}

/** Build a path for a bar with only its top corners rounded. */
function roundedTopBar(
  x: number,
  y: number,
  w: number,
  h: number,
  r: number,
): string {
  const rr = Math.max(0, Math.min(r, w / 2, h));
  return `M${x},${y + h} L${x},${y + rr} Q${x},${y} ${x + rr},${y} L${x + w - rr},${y} Q${x + w},${y} ${x + w},${y + rr} L${x + w},${y + h} Z`;
}

/** Compact area trend used inside KPI cards and section headers. */
export function AreaTrend({
  data,
  height = 180,
  valueKey = "count",
  labelKey = "label",
}: {
  data: Array<Record<string, number | string>>;
  height?: number;
  valueKey?: string;
  labelKey?: string;
}) {
  const t = useChartTheme();
  const [ref, width] = useResizeWidth<HTMLDivElement>();
  const gradId = useMemo(
    () => `area-grad-${Math.random().toString(36).slice(2, 8)}`,
    [],
  );
  const [hover, setHover] = useState<number | null>(null);

  const m = { top: 8, right: 12, bottom: 22, left: 36 };
  const innerW = Math.max(0, width - m.left - m.right);
  const innerH = Math.max(0, height - m.top - m.bottom);

  const points = data.map((d) => ({
    label: String(d[labelKey] ?? ""),
    value: Number(d[valueKey] ?? 0),
  }));
  const x = scalePoint<string>()
    .domain(points.map((_, i) => String(i)))
    .range([0, innerW]);
  const yMax = d3max(points, (d) => d.value) ?? 0;
  const y = scaleLinear()
    .domain([0, yMax || 1])
    .nice()
    .range([innerH, 0]);

  const px = (i: number) => x(String(i)) ?? 0;
  const areaGen = d3area<{ value: number }>()
    .x((_, i) => px(i))
    .y0(innerH)
    .y1((d) => y(d.value))
    .curve(curveMonotoneX);
  const lineGen = d3line<{ value: number }>()
    .x((_, i) => px(i))
    .y((d) => y(d.value))
    .curve(curveMonotoneX);

  const yTicks = y.ticks(4).filter((v) => Number.isInteger(v));
  const lastIdx = points.length - 1;
  const xTickIdx = (
    points.length <= 1 ? [0] : [0, Math.round(lastIdx / 2), lastIdx]
  ).filter((v, i, a) => a.indexOf(v) === i);

  const onMove = (e: React.MouseEvent<SVGRectElement>) => {
    if (points.length === 0 || innerW <= 0) return;
    const rect = e.currentTarget.getBoundingClientRect();
    const rel = e.clientX - rect.left;
    const step = points.length > 1 ? innerW / (points.length - 1) : innerW;
    const idx = Math.max(
      0,
      Math.min(points.length - 1, Math.round(rel / step)),
    );
    setHover(idx);
  };

  const hoveredPoint = hover !== null ? points[hover] : null;

  return (
    <div
      ref={ref}
      className="d3-chart"
      style={{ position: "relative", width: "100%", height }}
    >
      {width > 0 && (
        <svg
          width={width}
          height={height}
          role="img"
          aria-label="Activity trend"
        >
          <defs>
            <linearGradient id={gradId} x1="0" y1="0" x2="0" y2="1">
              <stop offset="0%" stopColor={t.accent} stopOpacity={0.28} />
              <stop offset="100%" stopColor={t.accent} stopOpacity={0.02} />
            </linearGradient>
          </defs>
          <g transform={`translate(${m.left},${m.top})`}>
            {yTicks.map((v) => (
              <text
                key={v}
                x={-8}
                y={y(v)}
                dy="0.32em"
                textAnchor="end"
                fontSize={11}
                fill={t.muted}
              >
                {v}
              </text>
            ))}
            <line
              x1={0}
              y1={innerH}
              x2={innerW}
              y2={innerH}
              stroke={t.border}
            />
            {points.length > 0 && (
              <>
                <path
                  className="d3-area"
                  d={areaGen(points) ?? undefined}
                  fill={`url(#${gradId})`}
                />
                <path
                  className="d3-line"
                  d={lineGen(points) ?? undefined}
                  fill="none"
                  stroke={t.accent}
                  strokeWidth={2}
                />
              </>
            )}
            {xTickIdx.map((i) => (
              <text
                key={i}
                x={px(i)}
                y={innerH + 16}
                textAnchor="middle"
                fontSize={11}
                fill={t.muted}
              >
                {points[i]?.label}
              </text>
            ))}
            {hoveredPoint && (
              <>
                <line
                  x1={px(hover!)}
                  y1={0}
                  x2={px(hover!)}
                  y2={innerH}
                  stroke={t.border}
                />
                <circle
                  cx={px(hover!)}
                  cy={y(hoveredPoint.value)}
                  r={3.5}
                  fill={t.accent}
                  stroke={t.surface}
                  strokeWidth={2}
                />
              </>
            )}
            <rect
              x={0}
              y={0}
              width={innerW}
              height={innerH}
              fill="transparent"
              onMouseMove={onMove}
              onMouseLeave={() => setHover(null)}
            />
          </g>
        </svg>
      )}
      {hoveredPoint && (
        <FloatingTooltip
          x={m.left + px(hover!)}
          y={m.top + y(hoveredPoint.value)}
          width={width}
          t={t}
          label={hoveredPoint.label}
          payload={[
            { name: "Events", value: hoveredPoint.value, color: t.accent },
          ]}
        />
      )}
    </div>
  );
}

export type DistributionDatum = {
  label: string;
  value: number;
  color?: string;
};

/** Vertical bar distribution (e.g. risk, tenant application counts). */
export function BarDistribution({
  data,
  height = 200,
  fallbackColor,
}: {
  data: DistributionDatum[];
  height?: number;
  fallbackColor?: string;
}) {
  const t = useChartTheme();
  const [ref, width] = useResizeWidth<HTMLDivElement>();
  const [hover, setHover] = useState<number | null>(null);
  const color = fallbackColor ?? t.accent;

  const leftM = 36;
  const rightM = 12;
  const topM = 8;
  const innerW = Math.max(0, width - leftM - rightM);
  const maxBarSize = 54;

  const x = scaleBand<string>()
    .domain(data.map((d) => d.label))
    .range([0, innerW])
    .padding(0.3);

  // Long labels overlap when packed horizontally, so estimate the widest
  // (clipped) label and, when it would not fit in a single bar's slot, slant
  // the labels and reserve extra vertical room for them.
  const maxLabelChars = 18;
  const clip = (s: string) =>
    s.length > maxLabelChars ? `${s.slice(0, maxLabelChars - 1)}…` : s;
  const charPx = 6.3;
  const step = data.length > 0 ? innerW / data.length : innerW;
  const longestLabelPx =
    (d3max(data, (d) => clip(d.label).length) ?? 0) * charPx;
  const angled = data.length > 0 && longestLabelPx > step - 6;
  const angleDeg = 35;
  const labelBand = angled
    ? Math.ceil(longestLabelPx * Math.sin((angleDeg * Math.PI) / 180)) + 10
    : 0;
  const bottomM = 24 + labelBand;
  // Grow the drawing surface by the reserved label band so the plotted bars
  // keep their intended height regardless of whether labels are slanted.
  const chartHeight = height + labelBand;
  const innerH = Math.max(0, chartHeight - topM - bottomM);

  const yMax = d3max(data, (d) => d.value) ?? 0;
  const y = scaleLinear()
    .domain([0, yMax || 1])
    .nice()
    .range([innerH, 0]);
  const yTicks = y.ticks(4).filter((v) => Number.isInteger(v));

  const bandW = x.bandwidth();
  const barW = Math.min(bandW, maxBarSize);

  return (
    <div
      ref={ref}
      className="d3-chart"
      style={{ position: "relative", width: "100%", height: chartHeight }}
    >
      {width > 0 && (
        <svg
          width={width}
          height={chartHeight}
          role="img"
          aria-label="Distribution"
        >
          <g transform={`translate(${leftM},${topM})`}>
            {yTicks.map((v) => (
              <text
                key={v}
                x={-8}
                y={y(v)}
                dy="0.32em"
                textAnchor="end"
                fontSize={11}
                fill={t.muted}
              >
                {v}
              </text>
            ))}
            <line
              x1={0}
              y1={innerH}
              x2={innerW}
              y2={innerH}
              stroke={t.border}
            />
            {data.map((d, i) => {
              const bandX = x(d.label) ?? 0;
              const bx = bandX + (bandW - barW) / 2;
              const by = y(d.value);
              const bh = innerH - by;
              const cx = bandX + bandW / 2;
              const clipped = clip(d.label);
              return (
                <g
                  key={d.label}
                  onMouseEnter={() => setHover(i)}
                  onMouseLeave={() => setHover(null)}
                >
                  <path
                    className="d3-bar"
                    d={roundedTopBar(bx, by, barW, bh, 6)}
                    fill={d.color ?? color}
                    opacity={hover === null || hover === i ? 1 : 0.55}
                  />
                  {angled ? (
                    <text
                      transform={`translate(${cx},${innerH + 12}) rotate(-${angleDeg})`}
                      textAnchor="end"
                      fontSize={11}
                      fill={t.muted}
                    >
                      {clipped}
                      {clipped !== d.label && <title>{d.label}</title>}
                    </text>
                  ) : (
                    <text
                      x={cx}
                      y={innerH + 16}
                      textAnchor="middle"
                      fontSize={11}
                      fill={t.muted}
                    >
                      {clipped}
                      {clipped !== d.label && <title>{d.label}</title>}
                    </text>
                  )}
                </g>
              );
            })}
          </g>
        </svg>
      )}
      {hover !== null && data[hover] && (
        <FloatingTooltip
          x={leftM + (x(data[hover].label) ?? 0) + bandW / 2}
          y={topM + y(data[hover].value)}
          width={width}
          t={t}
          label={data[hover].label}
          payload={[
            {
              name: "Count",
              value: data[hover].value,
              color: data[hover].color ?? color,
            },
          ]}
        />
      )}
    </div>
  );
}

export type DonutDatum = { label: string; value: number; color: string };

/** Donut chart with a centered figure, used for posture / effect splits. */
export function DonutChart({
  data,
  centerLabel,
  centerSub,
  size = 150,
}: {
  data: DonutDatum[];
  centerLabel?: string;
  centerSub?: string;
  size?: number;
}) {
  const t = useChartTheme();
  const [hover, setHover] = useState<number | null>(null);
  const total = data.reduce((sum, d) => sum + d.value, 0);

  const innerR = size * 0.33;
  const outerR = size * 0.48;
  const cx = size / 2;
  const cy = size / 2;

  const slices: DonutDatum[] =
    total === 0 ? [{ label: "None", value: 1, color: t.grid }] : data;
  const arcGen = d3arc<PieArcDatum<DonutDatum>>()
    .innerRadius(innerR)
    .outerRadius(outerR)
    .padAngle(total === 0 ? 0 : 0.03)
    .cornerRadius(2);
  const arcs = d3pie<DonutDatum>()
    .sort(null)
    .value((d) => d.value)(slices);

  return (
    <div
      style={{
        position: "relative",
        width: size,
        height: size,
        flex: "0 0 auto",
      }}
    >
      <svg width={size} height={size} role="img" aria-label="Donut chart">
        <g transform={`translate(${cx},${cy})`}>
          {arcs.map((a, i) => (
            <path
              key={i}
              className="d3-arc"
              d={arcGen(a) ?? undefined}
              fill={slices[i].color}
              stroke={t.surface}
              strokeWidth={2}
              opacity={total === 0 || hover === null || hover === i ? 1 : 0.55}
              onMouseEnter={() => total > 0 && setHover(i)}
              onMouseLeave={() => setHover(null)}
            />
          ))}
        </g>
      </svg>
      {hover !== null && total > 0 && data[hover] && (
        <div
          style={{
            position: "absolute",
            left: "50%",
            top: -6,
            transform: "translate(-50%, -100%)",
            pointerEvents: "none",
          }}
        >
          <ChartTooltip
            active
            payload={[
              {
                name: data[hover].label,
                value: data[hover].value,
                color: data[hover].color,
              },
            ]}
            surface={t.surface}
            border={t.border}
            textH={t.textH}
            muted={t.muted}
          />
        </div>
      )}
      {(centerLabel || centerSub) && (
        <div
          style={{
            position: "absolute",
            inset: 0,
            display: "flex",
            flexDirection: "column",
            alignItems: "center",
            justifyContent: "center",
            pointerEvents: "none",
          }}
        >
          {centerLabel && (
            <span
              style={{
                fontSize: 22,
                fontWeight: 800,
                color: t.textH,
                lineHeight: 1,
              }}
            >
              {centerLabel}
            </span>
          )}
          {centerSub && (
            <span
              style={{
                fontSize: 10,
                letterSpacing: "0.08em",
                textTransform: "uppercase",
                color: t.muted,
                marginTop: 2,
              }}
            >
              {centerSub}
            </span>
          )}
        </div>
      )}
    </div>
  );
}

/**
 * Local-calendar-date key (`YYYY-MM-DD`) for a date. Uses the viewer's local
 * calendar fields rather than `toISOString()` (which is UTC) so day bucketing
 * matches the viewer's "today" instead of a UTC day that may have already
 * rolled over for anyone east of UTC.
 */
function localDateKey(d: Date): string {
  const year = d.getFullYear();
  const month = String(d.getMonth() + 1).padStart(2, "0");
  const day = String(d.getDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

/**
 * Group audit events into per-day buckets for the trailing `days` window so we
 * can render an activity trend without any new backend endpoint. Buckets and
 * events are keyed by *local* calendar date, so the window ends on the viewer's
 * current day and same-day events land in the final bucket regardless of the
 * viewer's timezone offset from UTC.
 */
export function buildActivityTrend(
  events: Array<{ timestamp: string }>,
  days = 14,
): Array<{ label: string; count: number }> {
  const buckets = new Map<string, number>();
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  const order: Array<{ key: string; date: Date }> = [];
  for (let i = days - 1; i >= 0; i--) {
    const d = new Date(today);
    d.setDate(today.getDate() - i);
    const key = localDateKey(d);
    order.push({ key, date: d });
    buckets.set(key, 0);
  }
  for (const e of events) {
    const key = localDateKey(new Date(e.timestamp));
    if (buckets.has(key)) {
      buckets.set(key, (buckets.get(key) ?? 0) + 1);
    }
  }
  return order.map(({ key, date }) => ({
    label: date.toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
    }),
    count: buckets.get(key) ?? 0,
  }));
}

/**
 * Expand the backend AI-usage trend — which only contains the UTC days that
 * actually had invocations — into a continuous, zero-filled series covering the
 * trailing `days` window up to and including today. Keeps the UTC-date basis the
 * backend aggregates on so counts land on the correct day.
 */
export function buildUsageTrend(
  points: Array<{ date: string; count: number }>,
  days: number,
): Array<{ label: string; count: number }> {
  const counts = new Map<string, number>();
  for (const point of points) {
    const key = point.date.slice(0, 10);
    counts.set(key, (counts.get(key) ?? 0) + point.count);
  }
  const now = new Date();
  const anchor = new Date(
    Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()),
  );
  const series: Array<{ label: string; count: number }> = [];
  for (let i = days - 1; i >= 0; i--) {
    const d = new Date(anchor);
    d.setUTCDate(anchor.getUTCDate() - i);
    const key = d.toISOString().slice(0, 10);
    series.push({
      label: d.toLocaleDateString(undefined, {
        month: "short",
        day: "numeric",
        timeZone: "UTC",
      }),
      count: counts.get(key) ?? 0,
    });
  }
  return series;
}
