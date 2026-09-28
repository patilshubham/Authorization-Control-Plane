import { useEffect, useMemo, useState } from "react";
import { useAuditFeed, useAuditActivitySummary } from "../../api/hooks";
import { EmptyBlock, Spinner } from "../../components/primitives";
import { FilterBar, Pagination, usePageSizeState } from "../../ui";
import { dayBucket } from "../activity";
import { AuditEventCard } from "../AuditEventCard";
import { D3Heatmap } from "../../components/viz/D3Heatmap";
import { AUDIT_CATEGORIES } from "../../constants";

// ── Activity (audit) ─────────────────────────────────────────────────────────────

export function ActivityPanel({ appId }: { appId: string }) {
  const [query, setQuery] = useState("");
  const [category, setCategory] = useState<string>("");
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = usePageSizeState();
  const [expanded, setExpanded] = useState<string | null>(null);
  useEffect(() => setPage(1), [query, category, pageSize]);

  const feed = useAuditFeed({
    appId,
    q: query || undefined,
    category: category || undefined,
    page,
    pageSize,
  });
  const summary = useAuditActivitySummary({ appId });
  const filtered = feed.data?.items ?? [];
  const total = feed.data?.total ?? 0;
  const hasAny = (summary.data?.total ?? 0) > 0;

  const groups = useMemo(() => {
    const map = new Map<string, typeof filtered>();
    for (const e of filtered) {
      const bucket = dayBucket(e.timestamp);
      const list = map.get(bucket) ?? [];
      list.push(e);
      map.set(bucket, list);
    }
    return Array.from(map.entries());
  }, [filtered]);

  return (
    <>
      <header className="page-head">
        <div className="page-head-titles">
          <h1>Activity</h1>
          <p className="page-sub">
            Governance changes in this application, most recent first.
          </p>
        </div>
      </header>

      {feed.isLoading && !feed.data ? (
        <Spinner label="Loading activity…" />
      ) : !hasAny ? (
        <EmptyBlock
          title="No activity yet"
          hint="Governance changes will appear here."
        />
      ) : (
        <>
          <section className="panel">
            <div className="panel-head-row">
              <h2>Activity calendar</h2>
            </div>
            <div className="calendar-3q">
              <D3Heatmap
                dayCounts={summary.data?.days}
                weeks={16}
                ariaLabel="Application activity by day"
              />
            </div>
          </section>
          <FilterBar
            search={{
              value: query,
              onChange: setQuery,
              placeholder: "Search events, actor or subject…",
              label: "Search activity",
            }}
            filters={[
              {
                key: "category",
                label: "Category",
                value: category,
                onChange: setCategory,
                options: [
                  { value: "", label: "All categories" },
                  ...AUDIT_CATEGORIES.map((c) => ({ value: c, label: c })),
                ],
              },
            ]}
            onClearAll={() => {
              setQuery("");
              setCategory("");
            }}
          />
          {filtered.length === 0 ? (
            <EmptyBlock
              title="No matching events"
              hint="Try a different search or category."
            />
          ) : (
            <div className="activity-feed">
              {groups.map(([bucket, list]) => (
                <section key={bucket} className="activity-group">
                  <h2 className="activity-group-head">
                    {bucket}
                    <span className="muted">{list.length}</span>
                  </h2>
                  <ol className="activity-list">
                    {list.map((e, i) => {
                      const id = e.eventId ?? `${bucket}-${i}`;
                      return (
                        <AuditEventCard
                          key={id}
                          event={e}
                          scope="app"
                          id={id}
                          isOpen={expanded === id}
                          onToggle={(x) =>
                            setExpanded(expanded === x ? null : x)
                          }
                        />
                      );
                    })}
                  </ol>
                </section>
              ))}
            </div>
          )}
          {total > 0 && (
            <Pagination
              page={page}
              pageSize={pageSize}
              total={total}
              onPageChange={setPage}
              onPageSizeChange={setPageSize}
            />
          )}
        </>
      )}
    </>
  );
}
