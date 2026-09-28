/** Shared display formatters for the workspace panels and inspectors. */

import type { AssignmentSummary } from "../types";

/** An assignment is effectively expired when it is still ACTIVE but its validUntil has passed. */
export function isExpired(a: AssignmentSummary): boolean {
  return (
    a.state === "ACTIVE" &&
    !!a.validUntil &&
    new Date(a.validUntil).getTime() <= Date.now()
  );
}

/** The state to display: mirrors the runtime engine (ACTIVE past its expiry reads as EXPIRED). */
export function displayState(a: AssignmentSummary): string {
  return isExpired(a) ? "EXPIRED" : a.state;
}

/** Pretty-print a JSON string; returns the original value unchanged if it is not valid JSON. */
export function formatJson(value: string): string {
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

/** Format an ISO timestamp as a short, locale-aware date. Returns `fallback` when the value is empty. */
export function formatDate(iso: string | null, fallback = "—"): string {
  return iso
    ? new Date(iso).toLocaleDateString(undefined, {
        year: "numeric",
        month: "short",
        day: "numeric",
      })
    : fallback;
}
