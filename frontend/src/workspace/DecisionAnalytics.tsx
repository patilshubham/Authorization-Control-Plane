import { useState } from "react";
import { useDecisionAnalytics } from "../api/hooks";
import {
  AreaTrend,
  BarDistribution,
  DonutChart,
  useChartTheme,
} from "../components/charts";
import { EmptyBlock, Segmented, Spinner } from "../components/primitives";

// P7 — Runtime decision analytics. A read-only, aggregate view over the recorded
// `decisions` for this application (allow/deny mix, denial reasons, most-denied
// resources, daily volume). The enforcement path is untouched; this only reads.

export function DecisionAnalytics({ appId }: { appId: string }) {
  const [windowDays, setWindowDays] = useState<"7" | "30" | "90">("30");
  const analytics = useDecisionAnalytics(appId, Number(windowDays));
  const chart = useChartTheme();
  const data = analytics.data;

  return (
    <>
      <header className="page-head">
        <div className="page-head-titles">
          <h1>Decision analytics</h1>
          <p className="page-sub">
            Runtime authorization outcomes recorded for this application.
          </p>
        </div>
        <Segmented
          ariaLabel="Time window"
          value={windowDays}
          onChange={setWindowDays}
          options={[
            { value: "7", label: "7d" },
            { value: "30", label: "30d" },
            { value: "90", label: "90d" },
          ]}
        />
      </header>

      {analytics.isLoading ? (
        <Spinner label="Loading analytics…" />
      ) : analytics.isError ? (
        <EmptyBlock title="Analytics unavailable" hint="Try again shortly." />
      ) : !data || data.total === 0 ? (
        <EmptyBlock
          title="No decisions recorded"
          hint="Runtime decisions appear here once decision recording is enabled and traffic flows through /v1/authorize."
        />
      ) : (
        <>
          <div className="panel decision-stats">
            <div>
              <span className="muted">Total decisions</span>
              <strong>{data.total.toLocaleString()}</strong>
            </div>
            <div>
              <span className="muted">Allowed</span>
              <strong>{data.allowed.toLocaleString()}</strong>
            </div>
            <div>
              <span className="muted">Denied</span>
              <strong>{data.denied.toLocaleString()}</strong>
            </div>
            <div>
              <span className="muted">Allow rate</span>
              <strong>
                {Math.round((data.allowed / data.total) * 100)}%
              </strong>
            </div>
          </div>

          <div className="panel-columns">
            <section className="panel">
              <h2>Allow vs deny</h2>
              <div className="donut-wrap">
                <DonutChart
                  size={150}
                  centerLabel={`${Math.round((data.allowed / data.total) * 100)}%`}
                  centerSub="allowed"
                  data={[
                    {
                      label: "Allowed",
                      value: data.allowed,
                      color: chart.success,
                    },
                    {
                      label: "Denied",
                      value: data.denied,
                      color: chart.danger,
                    },
                  ]}
                />
              </div>
            </section>

            <section className="panel">
              <h2>Daily volume</h2>
              <AreaTrend
                height={190}
                data={data.daily.map((d) => ({
                  label: d.date,
                  count: d.allowed + d.denied,
                }))}
              />
            </section>
          </div>

          <div className="panel-columns">
            <section className="panel">
              <h2>Top denial reasons</h2>
              {data.topDenyReasons.length === 0 ? (
                <p className="muted">No denials in this window.</p>
              ) : (
                <BarDistribution
                  height={210}
                  fallbackColor={chart.danger}
                  data={data.topDenyReasons.map((r) => ({
                    label: r.label,
                    value: r.count,
                  }))}
                />
              )}
            </section>

            <section className="panel">
              <h2>Most-denied resources</h2>
              {data.topDeniedResources.length === 0 ? (
                <p className="muted">No denials in this window.</p>
              ) : (
                <BarDistribution
                  height={210}
                  fallbackColor={chart.warning}
                  data={data.topDeniedResources.map((r) => ({
                    label: r.label,
                    value: r.count,
                  }))}
                />
              )}
            </section>
          </div>
        </>
      )}
    </>
  );
}
