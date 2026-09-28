import { useEffect, useMemo, useRef, useState } from "react";
import { Kbd } from "../components/primitives";
import { AppIcon } from "../components/icons";

export type PaletteItem = {
  id: string;
  label: string;
  hint?: string;
  group: string;
  keywords?: string;
  run: () => void;
};

/** Lightweight subsequence fuzzy match with a relevance score. */
function score(query: string, text: string): number {
  if (!query) return 1;
  const q = query.toLowerCase();
  const t = text.toLowerCase();
  const direct = t.indexOf(q);
  if (direct === 0) return 1000;
  if (direct > 0) return 600 - direct;
  let ti = 0;
  let matched = 0;
  let streak = 0;
  let best = 0;
  for (let qi = 0; qi < q.length; qi++) {
    let found = false;
    while (ti < t.length) {
      if (t[ti] === q[qi]) {
        matched++;
        streak++;
        best = Math.max(best, streak);
        ti++;
        found = true;
        break;
      }
      streak = 0;
      ti++;
    }
    if (!found) return 0;
  }
  return matched === q.length ? 100 + best : 0;
}

export function CommandPalette({
  open,
  items,
  onClose,
}: {
  open: boolean;
  items: PaletteItem[];
  onClose: () => void;
}) {
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLUListElement>(null);

  useEffect(() => {
    if (open) {
      setQuery("");
      setActive(0);
      requestAnimationFrame(() => inputRef.current?.focus());
    }
  }, [open]);

  const results = useMemo(() => {
    const scored = items
      .map((item) => ({
        item,
        s: score(
          query,
          `${item.label} ${item.hint ?? ""} ${item.keywords ?? ""}`,
        ),
      }))
      .filter((r) => r.s > 0)
      .sort((a, b) => b.s - a.s)
      .slice(0, 40)
      .map((r) => r.item);
    return scored;
  }, [items, query]);

  useEffect(() => {
    setActive(0);
  }, [query]);

  useEffect(() => {
    listRef.current
      ?.querySelector<HTMLElement>(`[data-idx='${active}']`)
      ?.scrollIntoView({ block: "nearest" });
  }, [active]);

  if (!open) return null;

  const grouped = results.reduce<Record<string, PaletteItem[]>>((acc, item) => {
    (acc[item.group] ??= []).push(item);
    return acc;
  }, {});
  let runningIndex = -1;

  return (
    <div className="palette-backdrop" role="presentation" onClick={onClose}>
      <div
        className="palette"
        role="dialog"
        aria-modal="true"
        aria-label="Command palette"
        onClick={(e) => e.stopPropagation()}
        onKeyDown={(e) => {
          if (e.key === "ArrowDown") {
            e.preventDefault();
            setActive((a) => Math.min(a + 1, results.length - 1));
          } else if (e.key === "ArrowUp") {
            e.preventDefault();
            setActive((a) => Math.max(a - 1, 0));
          } else if (e.key === "Enter") {
            e.preventDefault();
            const chosen = results[active];
            if (chosen) {
              chosen.run();
              onClose();
            }
          } else if (e.key === "Escape") {
            onClose();
          }
        }}
      >
        <div className="palette-input-row">
          <span className="palette-search-icon" aria-hidden="true">
            <AppIcon name="search" size={18} />
          </span>
          <input
            ref={inputRef}
            className="palette-input"
            placeholder="Search roles, permissions, policies, or run a command…"
            aria-label="Search or run a command"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
          <Kbd>Esc</Kbd>
        </div>
        <ul
          className="palette-results"
          ref={listRef}
          role="listbox"
          aria-label="Results"
        >
          {results.length === 0 && (
            <li className="palette-empty">No matches.</li>
          )}
          {Object.entries(grouped).map(([group, groupItems]) => (
            <li key={group}>
              <p className="palette-group">{group}</p>
              <ul>
                {groupItems.map((item) => {
                  runningIndex++;
                  const idx = runningIndex;
                  return (
                    <li
                      key={item.id}
                      data-idx={idx}
                      role="option"
                      aria-selected={idx === active}
                      className={`palette-item${idx === active ? " active" : ""}`}
                      onMouseEnter={() => setActive(idx)}
                      onClick={() => {
                        item.run();
                        onClose();
                      }}
                    >
                      <span className="palette-item-label">{item.label}</span>
                      {item.hint && (
                        <span className="palette-item-hint">{item.hint}</span>
                      )}
                    </li>
                  );
                })}
              </ul>
            </li>
          ))}
        </ul>
        <footer className="palette-footer">
          <span>
            <Kbd>↑</Kbd>
            <Kbd>↓</Kbd> navigate
          </span>
          <span>
            <Kbd>↵</Kbd> select
          </span>
          <span>
            <Kbd>Esc</Kbd> close
          </span>
        </footer>
      </div>
    </div>
  );
}
