import { useEffect, useMemo, useState, type ReactNode } from "react";
import { SlideOver, useModalDialog } from "./components/primitives";
import { getRuntimeConfig } from "./api/runtimeConfig";

/**
 * Page-size state seeded from the server-owned default page size. Use this in
 * place of `useState(25)` so the initial rows-per-page is centrally configured
 * (via `GET /v1/config`) rather than hardcoded across every paged surface.
 */
export function usePageSizeState() {
  return useState<number>(() => getRuntimeConfig().pagination.defaultPageSize);
}

export function DataPanel({
  title,
  empty,
  children,
}: {
  title: string;
  empty: boolean;
  children: ReactNode;
}) {
  return (
    <section
      className="data-panel"
      aria-labelledby={`${title.replaceAll(" ", "-")}-title`}
    >
      <h2 id={`${title.replaceAll(" ", "-")}-title`}>{title}</h2>
      {empty ? <EmptyState /> : children}
    </section>
  );
}

export function EmptyState({
  message = "No records found.",
  action,
}: {
  message?: string;
  action?: { label: string; onClick: () => void };
}) {
  return (
    <div className="state-panel">
      <span>{message}</span>
      {action && (
        <button
          type="button"
          className="btn-primary"
          style={{ marginTop: "0.75rem" }}
          onClick={action.onClick}
        >
          {action.label}
        </button>
      )}
    </div>
  );
}

export function LoadingState({ label }: { label: string }) {
  return (
    <div className="state-panel" aria-busy="true">
      {label}
    </div>
  );
}

export function ErrorState({ message }: { message: string }) {
  return (
    <div className="state-panel error" role="alert">
      {message}
    </div>
  );
}

export function StatusChip({ value }: { value: string }) {
  return <span className={`status-chip ${value.toLowerCase()}`}>{value}</span>;
}

export function Notification({
  message,
  tone,
  onDismiss,
}: {
  message: string;
  tone: "success" | "error";
  onDismiss?: () => void;
}) {
  useEffect(() => {
    if (!onDismiss) return;
    const t = setTimeout(onDismiss, 4000);
    return () => clearTimeout(t);
  }, [onDismiss]);

  return (
    <div className={`notification ${tone}`} role="status">
      {message}
      {onDismiss && (
        <button
          type="button"
          className="notification-close"
          onClick={onDismiss}
          aria-label="Dismiss"
        >
          ×
        </button>
      )}
    </div>
  );
}

export function DrawerPanel({
  title,
  open,
  children,
  onClose,
  wide,
}: {
  title: string;
  open: boolean;
  children: ReactNode;
  onClose: () => void;
  wide?: boolean;
}) {
  // Reuse the SlideOver primitive so AI detail panels look and behave exactly
  // like the create flows (blurred/frozen background, bordered header, and a
  // scrollable body). Backdrop clicks are intentionally inert here — the panel
  // is dismissed only via the Close button or Escape.
  return (
    <SlideOver
      open={open}
      title={title}
      onClose={onClose}
      dismissOnBackdrop={false}
      wide={wide}
    >
      {children}
    </SlideOver>
  );
}

export function DialogPanel({
  title,
  open,
  children,
  onClose,
}: {
  title: string;
  open: boolean;
  children: ReactNode;
  onClose: () => void;
}) {
  const dialogRef = useModalDialog<HTMLElement>(open, onClose);
  if (!open) {
    return null;
  }

  return (
    <div className="dialog-backdrop" role="presentation">
      <section
        ref={dialogRef}
        tabIndex={-1}
        className="dialog-panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby="dialog-title"
      >
        <div className="drawer-header">
          <h2 id="dialog-title">{title}</h2>
          <button type="button" className="ghost" onClick={onClose}>
            Close
          </button>
        </div>
        {children}
      </section>
    </div>
  );
}

export function ConfirmDialog({
  title,
  message,
  confirmLabel = "Confirm",
  open,
  onConfirm,
  onCancel,
  danger,
  confirmDisabled,
}: {
  title: string;
  message: string;
  confirmLabel?: string;
  open: boolean;
  onConfirm: () => void;
  onCancel: () => void;
  danger?: boolean;
  confirmDisabled?: boolean;
}) {
  const dialogRef = useModalDialog<HTMLElement>(open, onCancel);
  if (!open) return null;
  return (
    <div className="dialog-backdrop" role="presentation">
      <section
        ref={dialogRef}
        tabIndex={-1}
        className="dialog-panel confirm-dialog"
        role="alertdialog"
        aria-modal="true"
        aria-labelledby="confirm-title"
      >
        <h2 id="confirm-title">{title}</h2>
        <p>{message}</p>
        <div className="dialog-actions">
          <button type="button" className="ghost" onClick={onCancel}>
            Cancel
          </button>
          <button
            type="button"
            className={danger ? "btn-danger" : "btn-primary"}
            onClick={onConfirm}
            disabled={confirmDisabled}
          >
            {confirmLabel}
          </button>
        </div>
      </section>
    </div>
  );
}

/**
 * Copies a value (e.g. an event or decision id) to the clipboard, showing a
 * brief "Copied" confirmation. Degrades silently where the Clipboard API is
 * unavailable (e.g. non-secure contexts).
 */
export function CopyButton({
  value,
  label = "Copy ID",
  copiedLabel = "Copied",
  className = "mini-btn ghost",
}: {
  value: string;
  label?: string;
  copiedLabel?: string;
  className?: string;
}) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      className={className}
      title={`Copy ${value}`}
      onClick={async () => {
        try {
          await navigator.clipboard.writeText(value);
          setCopied(true);
          window.setTimeout(() => setCopied(false), 1500);
        } catch {
          // Clipboard API unavailable (non-secure context) — no-op.
        }
      }}
    >
      {copied ? copiedLabel : label}
    </button>
  );
}

export function SkeletonTable({
  columns,
  rows = 5,
}: {
  columns: number;
  rows?: number;
}) {
  return (
    <table className="data-table" aria-busy="true" aria-label="Loading...">
      <thead>
        <tr>
          {Array.from({ length: columns }).map((_, i) => (
            <th key={i}>
              <span className="skeleton-cell" />
            </th>
          ))}
        </tr>
      </thead>
      <tbody>
        {Array.from({ length: rows }).map((_, r) => (
          <tr key={r} className="skeleton-row">
            {Array.from({ length: columns }).map((_, c) => (
              <td key={c}>
                <span className="skeleton-cell" />
              </td>
            ))}
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export function TabBar({
  tabs,
  active,
  onChange,
}: {
  tabs: Array<{ key: string; label: string }>;
  active: string;
  onChange: (key: string) => void;
}) {
  return (
    <nav className="tab-bar" aria-label="Sub-navigation">
      {tabs.map((tab) => (
        <button
          key={tab.key}
          type="button"
          className={`tab-item${active === tab.key ? " active" : ""}`}
          aria-current={active === tab.key ? "page" : undefined}
          onClick={() => onChange(tab.key)}
        >
          {tab.label}
        </button>
      ))}
    </nav>
  );
}

export function PageHeader({
  title,
  subtitle,
  action,
}: {
  title: string;
  subtitle?: string;
  action?: ReactNode;
}) {
  return (
    <div className="page-header">
      <div>
        <h1>{title}</h1>
        {subtitle && <p className="page-subtitle">{subtitle}</p>}
      </div>
      {action && <div className="page-header-action">{action}</div>}
    </div>
  );
}

export function StatCard({
  label,
  value,
  badge,
}: {
  label: string;
  value: string | number;
  badge?: string;
}) {
  return (
    <div className="stat-card">
      <div className="stat-value">{value}</div>
      <div className="stat-label">{label}</div>
      {badge && (
        <span className={`stat-badge ${badge.toLowerCase()}`}>{badge}</span>
      )}
    </div>
  );
}

export type FilterOption = { value: string; label: string };

export type FilterDef = {
  /** Stable identifier for the control. */
  key: string;
  /** Human label used for the aria-label and active-filter chips. */
  label: string;
  value: string;
  options: FilterOption[];
  onChange: (value: string) => void;
};

/**
 * Standardized filter row: an optional search box plus a set of select
 * dropdowns, with removable chips summarizing active filters and a single
 * "Clear all" affordance. Purely presentational over caller-owned state.
 */
export function FilterBar({
  search,
  filters,
  onClearAll,
}: {
  search?: {
    value: string;
    onChange: (value: string) => void;
    placeholder?: string;
    label?: string;
  };
  filters?: FilterDef[];
  onClearAll?: () => void;
}) {
  const active = (filters ?? []).filter((f) => f.value !== "");
  const hasActive = active.length > 0 || Boolean(search?.value);
  return (
    <div className="filter-bar-wrap">
      <div className="filter-bar">
        {search && (
          <input
            type="search"
            placeholder={search.placeholder ?? "Search…"}
            value={search.value}
            onChange={(e) => search.onChange(e.target.value)}
            aria-label={search.label ?? search.placeholder ?? "Search"}
          />
        )}
        {(filters ?? []).map((f) => (
          <select
            key={f.key}
            value={f.value}
            onChange={(e) => f.onChange(e.target.value)}
            aria-label={f.label}
            className={f.value !== "" ? "is-active" : undefined}
          >
            {f.options.map((o) => (
              <option key={o.value} value={o.value}>
                {o.label}
              </option>
            ))}
          </select>
        ))}
      </div>
      {hasActive && (
        <div className="filter-chips">
          {active.map((f) => {
            const opt = f.options.find((o) => o.value === f.value);
            return (
              <button
                key={f.key}
                type="button"
                className="filter-chip"
                onClick={() => f.onChange("")}
                aria-label={`Clear ${f.label} filter`}
              >
                <span className="filter-chip-label">{f.label}:</span>{" "}
                {opt?.label ?? f.value}
                <span className="filter-chip-x" aria-hidden="true">
                  ×
                </span>
              </button>
            );
          })}
          {onClearAll && (
            <button
              type="button"
              className="filter-clear-all"
              onClick={onClearAll}
            >
              Clear all
            </button>
          )}
        </div>
      )}
    </div>
  );
}

export type DataTableColumn<T> = {
  /** Stable identifier for the column (used for sort state and React keys). */
  key: string;
  header: string;
  /** Custom cell renderer. Falls back to the string form of `sortValue`. */
  render?: (row: T) => ReactNode;
  /** Providing this makes the column sortable and contributes to search. */
  sortValue?: (row: T) => string | number;
  /** Explicit text used for global search (defaults to `sortValue`). */
  searchValue?: (row: T) => string;
  className?: string;
};

/**
 * Standalone pagination bar (rows-per-page selector, range readout, prev/next).
 * Used inside {@link DataTable} and by the audit/activity feeds, which render
 * bespoke lists rather than a table but share the same paging affordance.
 */
export function Pagination({
  page,
  pageSize,
  total,
  onPageChange,
  onPageSizeChange,
  pageSizeOptions = getRuntimeConfig().pagination.pageSizeOptions,
}: {
  page: number;
  pageSize: number;
  total: number;
  onPageChange: (page: number) => void;
  onPageSizeChange: (pageSize: number) => void;
  pageSizeOptions?: number[];
}) {
  const totalPages = Math.max(1, Math.ceil(total / pageSize));
  return (
    <div className="table-pagination">
      <label className="page-size">
        Rows
        <select
          value={pageSize}
          onChange={(e) => onPageSizeChange(Number(e.target.value))}
        >
          {pageSizeOptions.map((n) => (
            <option key={n} value={n}>
              {n}
            </option>
          ))}
        </select>
      </label>
      <span className="page-range">
        {(page - 1) * pageSize + 1}–{Math.min(page * pageSize, total)} of {total}
      </span>
      <div className="page-nav">
        <button
          type="button"
          className="page-btn"
          onClick={() => onPageChange(page - 1)}
          disabled={page <= 1}
          aria-label="Previous page"
        >
          ‹
        </button>
        <span className="page-indicator">
          {page} / {totalPages}
        </span>
        <button
          type="button"
          className="page-btn"
          onClick={() => onPageChange(page + 1)}
          disabled={page >= totalPages}
          aria-label="Next page"
        >
          ›
        </button>
      </div>
    </div>
  );
}

/**
 * Reusable, presentational table for backend-fetched collections. Renders the
 * standard list toolbar (search + filters + result count), column sorting and
 * pagination over data already loaded from the API — no data is fabricated
 * here. Loading/error/empty states are handled internally.
 *
 * Search is uncontrolled (filters rows internally) unless `onSearchChange` is
 * supplied, in which case the caller owns the query (e.g. server-side search)
 * and is responsible for pre-filtering `rows`.
 */
export function DataTable<T>({
  columns,
  rows,
  getRowKey,
  isLoading = false,
  isError = false,
  errorMessage = "Could not load data.",
  emptyMessage,
  emptyAction,
  rowActions,
  searchable = true,
  searchPlaceholder = "Search…",
  searchValue,
  onSearchChange,
  filters,
  onClearFilters,
  pageSize: initialPageSize = getRuntimeConfig().pagination.defaultPageSize,
  selectable = false,
  bulkActions,
  serverPagination,
}: {
  columns: DataTableColumn<T>[];
  rows: T[];
  getRowKey: (row: T, index: number) => string;
  isLoading?: boolean;
  isError?: boolean;
  errorMessage?: string;
  emptyMessage?: string;
  emptyAction?: { label: string; onClick: () => void };
  rowActions?: (row: T) => ReactNode;
  searchable?: boolean;
  searchPlaceholder?: string;
  /** Controlled search value. Provide together with `onSearchChange`. */
  searchValue?: string;
  /** When provided, search is controlled by the caller (no internal filtering). */
  onSearchChange?: (value: string) => void;
  /** Standardized filter dropdowns shown in the toolbar with removable chips. */
  filters?: FilterDef[];
  /** Clears all filters (renders a "Clear all" affordance when filters active). */
  onClearFilters?: () => void;
  /** Rows per page; a page-size selector is offered around this default. */
  pageSize?: number;
  /** Enables a leading selection checkbox column and bulk actions. */
  selectable?: boolean;
  /** Renders a bulk action bar when rows are selected. */
  bulkActions?: (selected: T[], clearSelection: () => void) => ReactNode;
  /**
   * Server-driven pagination. When provided, `rows` is treated as the current
   * page only (already sliced by the server); the table skips internal slicing
   * and drives the pagination bar from these values. The caller is responsible
   * for fetching each page. Pair with server-side search/filter (or disable
   * `searchable`) to avoid misleading within-page filtering.
   */
  serverPagination?: {
    page: number;
    pageSize: number;
    total: number;
    onPageChange: (page: number) => void;
    onPageSizeChange: (size: number) => void;
  };
}) {
  const [internalQuery, setInternalQuery] = useState("");
  const [sort, setSort] = useState<{ key: string; dir: "asc" | "desc" } | null>(
    null,
  );
  const [pageSize, setPageSize] = useState(initialPageSize);
  const [page, setPage] = useState(1);
  const [selected, setSelected] = useState<Set<string>>(() => new Set());

  const controlledSearch = onSearchChange !== undefined;
  const query = controlledSearch ? (searchValue ?? "") : internalQuery;
  const setQuery = controlledSearch ? onSearchChange : setInternalQuery;

  const visibleColumns = columns;
  const columnCount =
    visibleColumns.length + (rowActions ? 1 : 0) + (selectable ? 1 : 0);

  const filtered = useMemo(() => {
    // Controlled search means the caller already filtered `rows`.
    if (controlledSearch) return rows;
    const q = query.trim().toLowerCase();
    if (!q) return rows;
    return rows.filter((row) =>
      columns.some((col) => {
        const value =
          col.searchValue?.(row) ??
          (col.sortValue ? String(col.sortValue(row)) : "");
        return value.toLowerCase().includes(q);
      }),
    );
  }, [rows, columns, query, controlledSearch]);

  const sorted = useMemo(() => {
    if (!sort) return filtered;
    const col = columns.find((c) => c.key === sort.key);
    if (!col?.sortValue) return filtered;
    const accessor = col.sortValue;
    const factor = sort.dir === "asc" ? 1 : -1;
    return [...filtered].sort((a, b) => {
      const av = accessor(a);
      const bv = accessor(b);
      if (av < bv) return -1 * factor;
      if (av > bv) return 1 * factor;
      return 0;
    });
  }, [filtered, columns, sort]);

  const totalPages = Math.max(1, Math.ceil(sorted.length / pageSize));
  const currentPage = Math.min(page, totalPages);
  const paged = useMemo(
    () =>
      serverPagination
        ? sorted
        : sorted.slice((currentPage - 1) * pageSize, currentPage * pageSize),
    [sorted, currentPage, pageSize, serverPagination],
  );

  // Reset to the first page whenever the result set materially changes.
  const filterSig = (filters ?? []).map((f) => f.value).join("|");
  useEffect(() => {
    setPage(1);
  }, [query, pageSize, sort, filterSig]);

  if (isLoading) return <SkeletonTable columns={columnCount} />;
  if (isError) return <ErrorState message={errorMessage} />;

  const toggleSort = (key: string) => {
    setSort((prev) =>
      prev?.key === key
        ? { key, dir: prev.dir === "asc" ? "desc" : "asc" }
        : { key, dir: "asc" },
    );
  };

  const clearSelection = () => setSelected(new Set());
  const toggleRow = (key: string) =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  const pageKeys = paged.map((row, i) =>
    getRowKey(row, (currentPage - 1) * pageSize + i),
  );
  const allPageSelected =
    pageKeys.length > 0 && pageKeys.every((k) => selected.has(k));
  const togglePageSelection = () =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (allPageSelected) pageKeys.forEach((k) => next.delete(k));
      else pageKeys.forEach((k) => next.add(k));
      return next;
    });
  const selectedRows = sorted.filter((row, i) =>
    selected.has(getRowKey(row, i)),
  );

  const activeFilters = (filters ?? []).filter((f) => f.value !== "");
  const hasActiveFilters = activeFilters.length > 0;
  const hasFilters = (filters ?? []).length > 0;
  const hasTools = searchable || hasFilters;

  return (
    <div className="data-table-wrap">
      {hasTools && (
        <div className="table-toolbar-wrap">
          <div className="table-toolbar">
            {searchable && (
              <input
                type="search"
                className="table-search"
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder={searchPlaceholder}
                aria-label="Search table"
              />
            )}
            {(filters ?? []).map((f) => (
              <select
                key={f.key}
                className={`table-filter${f.value !== "" ? " is-active" : ""}`}
                value={f.value}
                onChange={(e) => f.onChange(e.target.value)}
                aria-label={f.label}
              >
                {f.options.map((o) => (
                  <option key={o.value} value={o.value}>
                    {o.label}
                  </option>
                ))}
              </select>
            ))}
            {(query || hasActiveFilters) && (
              <span className="table-count">
                {sorted.length} result{sorted.length === 1 ? "" : "s"}
              </span>
            )}
          </div>
          {hasActiveFilters && (
            <div className="filter-chips">
              {activeFilters.map((f) => {
                const opt = f.options.find((o) => o.value === f.value);
                return (
                  <button
                    key={f.key}
                    type="button"
                    className="filter-chip"
                    onClick={() => f.onChange("")}
                    aria-label={`Clear ${f.label} filter`}
                  >
                    <span className="filter-chip-label">{f.label}:</span>{" "}
                    {opt?.label ?? f.value}
                    <span className="filter-chip-x" aria-hidden="true">
                      ×
                    </span>
                  </button>
                );
              })}
              {onClearFilters && (
                <button
                  type="button"
                  className="filter-clear-all"
                  onClick={onClearFilters}
                >
                  Clear all
                </button>
              )}
            </div>
          )}
        </div>
      )}
      {selectable && selected.size > 0 && (
        <div className="bulk-bar" role="status">
          <span>{selected.size} selected</span>
          <div className="bulk-bar-actions">
            {bulkActions?.(selectedRows, clearSelection)}
            <button
              type="button"
              className="mini-btn ghost"
              onClick={clearSelection}
            >
              Clear
            </button>
          </div>
        </div>
      )}
      <div className="table-scroll">
        <table className="data-table">
          <thead>
            <tr>
              {selectable && (
                <th className="col-select">
                  <input
                    type="checkbox"
                    checked={allPageSelected}
                    onChange={togglePageSelection}
                    aria-label="Select all rows on this page"
                  />
                </th>
              )}
              {visibleColumns.map((col) => {
                const active = sort?.key === col.key;
                return (
                  <th
                    key={col.key}
                    className={col.className}
                    aria-sort={
                      col.sortValue
                        ? active
                          ? sort!.dir === "asc"
                            ? "ascending"
                            : "descending"
                          : "none"
                        : undefined
                    }
                  >
                    {col.sortValue ? (
                      <button
                        type="button"
                        className="th-sort"
                        onClick={() => toggleSort(col.key)}
                      >
                        {col.header}
                        <span className="sort-indicator" aria-hidden="true">
                          {active ? (sort!.dir === "asc" ? "▲" : "▼") : "↕"}
                        </span>
                      </button>
                    ) : (
                      col.header
                    )}
                  </th>
                );
              })}
              {rowActions && <th>Actions</th>}
            </tr>
          </thead>
          <tbody>
            {paged.length === 0 ? (
              <tr>
                <td colSpan={columnCount}>
                  <EmptyState message={emptyMessage} action={emptyAction} />
                </td>
              </tr>
            ) : (
              paged.map((row, index) => {
                const rowKey = getRowKey(
                  row,
                  (currentPage - 1) * pageSize + index,
                );
                const isSelected = selected.has(rowKey);
                return (
                  <tr
                    key={rowKey}
                    className={isSelected ? "is-selected" : undefined}
                  >
                    {selectable && (
                      <td className="col-select">
                        <input
                          type="checkbox"
                          checked={isSelected}
                          onChange={() => toggleRow(rowKey)}
                          aria-label="Select row"
                        />
                      </td>
                    )}
                    {visibleColumns.map((col) => (
                      <td key={col.key} className={col.className}>
                        {col.render
                          ? col.render(row)
                          : col.sortValue
                            ? String(col.sortValue(row))
                            : null}
                      </td>
                    ))}
                    {rowActions && (
                      <td className="row-actions">{rowActions(row)}</td>
                    )}
                  </tr>
                );
              })
            )}
          </tbody>
        </table>
      </div>
      {sorted.length > 0 &&
        (serverPagination
          ? serverPagination.total > 0
          : sorted.length > pageSize || pageSize !== initialPageSize) &&
        (() => {
          const sp = serverPagination;
          return (
            <Pagination
              page={sp ? sp.page : currentPage}
              pageSize={sp ? sp.pageSize : pageSize}
              total={sp ? sp.total : sorted.length}
              onPageChange={(p) => (sp ? sp.onPageChange(p) : setPage(p))}
              onPageSizeChange={(n) =>
                sp ? sp.onPageSizeChange(n) : setPageSize(n)
              }
            />
          );
        })()}
    </div>
  );
}
