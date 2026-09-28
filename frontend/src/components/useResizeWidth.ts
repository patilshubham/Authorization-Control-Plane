import { useEffect, useRef, useState } from "react";

/**
 * Observe an element's width so SVG visualizations can size themselves without
 * a charting-library container. Replaces recharts' <ResponsiveContainer>.
 *
 * Returns a ref to attach to the sizing element and its current pixel width.
 * Falls back to `initial` before the first measurement and in environments
 * without ResizeObserver (e.g. jsdom during unit tests).
 */
export function useResizeWidth<T extends HTMLElement = HTMLDivElement>(
  initial = 320,
): [React.RefObject<T | null>, number] {
  const ref = useRef<T>(null);
  const [width, setWidth] = useState(initial);

  useEffect(() => {
    const el = ref.current;
    if (!el) return;

    const measure = () => {
      const w = el.clientWidth;
      if (w > 0) setWidth(w);
    };
    measure();

    if (typeof ResizeObserver === "undefined") return;
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  return [ref, width];
}
