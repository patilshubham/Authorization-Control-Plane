import { Link, useNavigate } from "react-router-dom";
import { AppIcon } from "../components/icons";
import { CopyButton } from "../ui";
import type { AuditEventSummary } from "../types";
import { describeEvent, relativeTime, actorLabel } from "./activity";
import { formatJson } from "./formatters";

// Shared, expandable audit-event row used by both the application Activity panel and the
// platform Audit feed so the two surfaces stay visually and behaviourally consistent. The row
// expands to reveal the before/after value diff, correlation id, a copy-id action and contextual
// deep-links (open the affected application and/or the target user).
export function AuditEventCard({
  event,
  scope,
  id,
  isOpen,
  onToggle,
}: {
  event: AuditEventSummary;
  /** "app" hides the redundant application label; "platform" shows which app the event belongs to. */
  scope: "app" | "platform";
  id: string;
  isOpen: boolean;
  onToggle: (id: string) => void;
}) {
  const navigate = useNavigate();
  const meta = describeEvent(event.eventType);
  const showAppLink = scope === "platform" && !!event.applicationId;
  const hasDetail = Boolean(
    event.oldValue ||
      event.newValue ||
      event.correlationId ||
      event.eventId ||
      event.targetSubjectEmail ||
      showAppLink,
  );

  return (
    <li className={`activity-card ${isOpen ? "is-open" : ""}`}>
      <button
        type="button"
        className="activity-card-main"
        aria-expanded={hasDetail ? isOpen : undefined}
        disabled={!hasDetail}
        onClick={() => hasDetail && onToggle(id)}
      >
        <span className={`activity-icon tone-${meta.tone}`} aria-hidden="true">
          {meta.icon}
        </span>
        <span className="activity-card-body">
          <span className="activity-card-title">
            {meta.label}
            <span className={`activity-cat tone-${meta.tone}`}>
              {meta.category}
            </span>
          </span>
          <span className="muted activity-card-meta">
            {scope === "platform"
              ? `${event.applicationId || "platform"} · `
              : ""}
            {actorLabel(event.actorEmail, event.actorRole)}
            {event.targetSubjectEmail ? ` → ${event.targetSubjectEmail}` : ""}
          </span>
        </span>
        <span
          className="activity-card-time muted"
          title={new Date(event.timestamp).toLocaleString()}
        >
          {relativeTime(event.timestamp)}
        </span>
        {hasDetail && (
          <span
            className={`activity-chevron${isOpen ? " open" : ""}`}
            aria-hidden="true"
          >
            <AppIcon name="chevron" size={14} />
          </span>
        )}
      </button>
      {isOpen && hasDetail && (
        <div className="activity-detail">
          {event.oldValue && (
            <div>
              <span className="activity-detail-label">Before</span>
              <pre className="code-block">{formatJson(event.oldValue)}</pre>
            </div>
          )}
          {event.newValue && (
            <div>
              <span className="activity-detail-label">After</span>
              <pre className="code-block">{formatJson(event.newValue)}</pre>
            </div>
          )}
          {event.correlationId && (
            <p className="muted">Correlation: {event.correlationId}</p>
          )}
          <div className="activity-detail-refs">
            {event.eventId && (
              <CopyButton value={event.eventId} label="Copy event ID" />
            )}
            {showAppLink && (
              <Link
                className="mini-btn ghost"
                to={`/app/${encodeURIComponent(event.applicationId)}`}
              >
                Open app
              </Link>
            )}
            {event.targetSubjectEmail && (
              <button
                type="button"
                className="mini-btn ghost"
                onClick={() =>
                  navigate(
                    `/platform/users/${encodeURIComponent(event.targetSubjectEmail!)}`,
                  )
                }
              >
                Open user
              </button>
            )}
          </div>
        </div>
      )}
    </li>
  );
}
