// D3Tree — a reusable horizontal node-link tree.
//
// Follows the "D3 computes, React renders" pattern: d3-hierarchy lays the tree
// out and d3-zoom drives pan/zoom, but every node and edge is rendered as plain
// React SVG so the markup stays declarative, themeable, and testable in jsdom.

import { useEffect, useMemo, useRef, useState } from "react";
import { hierarchy, tree as d3tree } from "d3-hierarchy";
import type { HierarchyPointNode } from "d3-hierarchy";
import { select as d3select } from "d3-selection";
import { zoom as d3zoom, zoomIdentity } from "d3-zoom";
import type { ZoomBehavior, ZoomTransform } from "d3-zoom";
import { useChartTheme } from "../charts";
import { NodeGlyph, nodeColor, type GraphNodeDatum } from "./nodes";

const NODE_W = 178;
const NODE_H = 46;
const ROW_GAP = 20; // vertical gap between sibling rows
const COL_GAP = 66; // horizontal gap between depth columns
const PAD = 24;

function truncate(text: string, max = 24): string {
  return text.length > max ? `${text.slice(0, max - 1)}…` : text;
}

function linkPath(
  s: HierarchyPointNode<GraphNodeDatum>,
  t: HierarchyPointNode<GraphNodeDatum>,
): string {
  // Horizontal orientation: d3's node.y is the depth axis, node.x the sibling axis.
  const x0 = s.y + NODE_W;
  const y0 = s.x;
  const x1 = t.y;
  const y1 = t.x;
  const mx = (x0 + x1) / 2;
  return `M${x0},${y0} C${mx},${y0} ${mx},${y1} ${x1},${y1}`;
}

export function D3Tree({
  data,
  height = 460,
  selectedId,
  onSelect,
  ariaLabel = "Relationship graph",
}: {
  data: GraphNodeDatum;
  height?: number;
  selectedId?: string;
  onSelect?: (node: GraphNodeDatum) => void;
  ariaLabel?: string;
}) {
  const t = useChartTheme();
  const svgRef = useRef<SVGSVGElement | null>(null);
  const zoomRef = useRef<ZoomBehavior<SVGSVGElement, unknown> | null>(null);
  const [transform, setTransform] = useState<ZoomTransform>(zoomIdentity);

  const layout = useMemo(() => {
    const root = hierarchy<GraphNodeDatum>(data);
    d3tree<GraphNodeDatum>().nodeSize([NODE_H + ROW_GAP, NODE_W + COL_GAP])(
      root as HierarchyPointNode<GraphNodeDatum>,
    );
    const pnodes = (root as HierarchyPointNode<GraphNodeDatum>).descendants();
    const links = (root as HierarchyPointNode<GraphNodeDatum>).links();
    let minX = Infinity;
    let maxX = -Infinity;
    let maxY = 0;
    for (const n of pnodes) {
      minX = Math.min(minX, n.x);
      maxX = Math.max(maxX, n.x);
      maxY = Math.max(maxY, n.y);
    }
    if (!Number.isFinite(minX)) {
      minX = 0;
      maxX = 0;
    }
    return {
      nodes: pnodes,
      links,
      contentW: maxY + NODE_W + PAD * 2,
      contentH: maxX - minX + NODE_H + PAD * 2,
      offsetX: PAD,
      offsetY: PAD - minX,
    };
  }, [data]);

  // Attach d3-zoom to the svg; write the resulting transform back into React state.
  useEffect(() => {
    if (!svgRef.current) return;
    const sel = d3select(svgRef.current);
    const z = d3zoom<SVGSVGElement, unknown>()
      .scaleExtent([0.4, 2.5])
      .on("zoom", (event) => setTransform(event.transform));
    zoomRef.current = z;
    sel.call(z);
    return () => {
      sel.on(".zoom", null);
    };
  }, []);

  const zoomBy = (factor: number) => {
    if (!svgRef.current || !zoomRef.current) return;
    zoomRef.current.scaleBy(d3select(svgRef.current), factor);
  };
  const resetZoom = () => {
    if (!svgRef.current || !zoomRef.current) return;
    zoomRef.current.transform(d3select(svgRef.current), zoomIdentity);
  };

  const glyphColor = (kind: GraphNodeDatum["kind"]) => nodeColor(kind, t);

  return (
    <div className="d3tree" style={{ height }}>
      <div className="d3tree-controls" role="group" aria-label="Zoom controls">
        <button
          type="button"
          className="d3tree-zoom-btn"
          aria-label="Zoom in"
          onClick={() => zoomBy(1.25)}
        >
          +
        </button>
        <button
          type="button"
          className="d3tree-zoom-btn"
          aria-label="Zoom out"
          onClick={() => zoomBy(0.8)}
        >
          −
        </button>
        <button
          type="button"
          className="d3tree-zoom-btn"
          aria-label="Reset view"
          onClick={resetZoom}
        >
          ⤢
        </button>
      </div>
      <svg
        ref={svgRef}
        className="d3tree-svg"
        width="100%"
        height={height}
        viewBox={`0 0 ${Math.max(layout.contentW, 320)} ${Math.max(layout.contentH, 200)}`}
        preserveAspectRatio="xMinYMid meet"
        role="tree"
        aria-label={ariaLabel}
      >
        <g
          transform={`translate(${transform.x},${transform.y}) scale(${transform.k})`}
        >
          <g transform={`translate(${layout.offsetX},${layout.offsetY})`}>
            {layout.links.map((l, i) => (
              <path
                key={i}
                className="d3tree-link"
                d={linkPath(l.source, l.target)}
                fill="none"
                stroke={t.border}
              />
            ))}
            {layout.nodes.map((n) => {
              const d = n.data;
              const selected = selectedId != null && d.id === selectedId;
              const interactive = d.kind !== "root" && !!onSelect;
              return (
                <g
                  key={d.id}
                  className={`d3tree-node${selected ? " is-selected" : ""}${interactive ? " is-interactive" : ""}`}
                  transform={`translate(${n.y},${n.x - NODE_H / 2})`}
                  role="treeitem"
                  aria-selected={selected}
                  aria-label={`${d.kind}: ${d.label}`}
                  tabIndex={interactive ? 0 : -1}
                  onClick={interactive ? () => onSelect?.(d) : undefined}
                  onKeyDown={
                    interactive
                      ? (e) => {
                          if (e.key === "Enter" || e.key === " ") {
                            e.preventDefault();
                            onSelect?.(d);
                          }
                        }
                      : undefined
                  }
                >
                  <rect
                    className="d3tree-card"
                    x={0}
                    y={0}
                    width={NODE_W}
                    height={NODE_H}
                    rx={10}
                  />
                  <rect
                    className="d3tree-accent"
                    x={0}
                    y={0}
                    width={4}
                    height={NODE_H}
                    rx={2}
                    fill={glyphColor(d.kind)}
                  />
                  <g
                    transform={`translate(14,${NODE_H / 2 - 7})`}
                    style={{ color: glyphColor(d.kind) }}
                  >
                    <NodeGlyph kind={d.kind} />
                  </g>
                  <text
                    className="d3tree-label"
                    x={36}
                    y={d.sublabel ? NODE_H / 2 - 3 : NODE_H / 2 + 4}
                  >
                    {truncate(d.label)}
                  </text>
                  {d.sublabel && (
                    <text className="d3tree-sub" x={36} y={NODE_H / 2 + 12}>
                      {truncate(d.sublabel, 26)}
                    </text>
                  )}
                  {d.riskLevel && (
                    <circle
                      className="d3tree-risk"
                      cx={NODE_W - 13}
                      cy={13}
                      r={4}
                      fill={
                        t.risk[
                          d.riskLevel.toUpperCase() as keyof typeof t.risk
                        ] ?? t.muted
                      }
                    />
                  )}
                  {typeof d.badge === "number" && d.badge > 0 && (
                    <text
                      className="d3tree-badge"
                      x={NODE_W - 12}
                      y={NODE_H - 10}
                      textAnchor="end"
                    >
                      {d.badge}
                    </text>
                  )}
                </g>
              );
            })}
          </g>
        </g>
      </svg>
    </div>
  );
}
