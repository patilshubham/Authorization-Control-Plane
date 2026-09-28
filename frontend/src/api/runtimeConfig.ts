// Runtime configuration singleton. The server (`GET /v1/config`) is the single
// source of truth for pagination, cache freshness, UI windows, and role labels.
// Plain modules (apiClient, activity) and synchronous state initializers can't
// await a React Query, so we cache the last-fetched config here behind sane
// defaults that mirror historical behaviour — nothing regresses before the
// config loads, and everything becomes server-driven once it does.

import type { AiConfig, PortalConfig } from "../types";

/** Fully-disabled AI config, used while loading, on error, or when AI is off. */
export const AI_DISABLED: AiConfig = {
  enabled: false,
  provider: null,
  model: null,
  limits: null,
  features: {
    policyAuthoring: false,
    decisionExplainer: false,
    impactAnalysis: false,
    configAdvisor: false,
    accessSearch: false,
    sodAnalysis: false,
    accessCertification: false,
    auditNarrative: false,
  },
};

/** Defaults matching the server's `PortalOptions` code defaults. */
export const DEFAULT_PORTAL_CONFIG: PortalConfig = {
  ai: AI_DISABLED,
  pagination: {
    defaultPageSize: 25,
    maxPageSize: 200,
    pageSizeOptions: [10, 25, 50, 100],
  },
  cache: {
    defaultStaleMs: 30_000,
    volatileStaleMs: 10_000,
    configStaleMs: 300_000,
  },
  ui: {
    aiReportingWindows: [7, 30, 90],
    activityTrendDays: 14,
    auditPageSize: 200,
  },
  roleLabels: {
    platformsuperadmin: "Platform Super Admin",
    platformreadonlyviewer: "Platform Read-only Viewer",
    applicationadmin: "Application Admin",
    readonlyviewer: "Read-only Viewer",
  },
};

let current: PortalConfig = DEFAULT_PORTAL_CONFIG;

/** The current runtime config (defaults until the server config has loaded). */
export function getRuntimeConfig(): PortalConfig {
  return current;
}

/**
 * Adopt a freshly-fetched config. Each section is merged over the defaults so a
 * partial payload (or a test fixture supplying only `ai`) never leaves a section
 * undefined — keeping every consumer resilient.
 */
export function setRuntimeConfig(config: Partial<PortalConfig> | undefined): void {
  if (!config) return;
  current = {
    ai: config.ai ?? DEFAULT_PORTAL_CONFIG.ai,
    pagination: { ...DEFAULT_PORTAL_CONFIG.pagination, ...config.pagination },
    cache: { ...DEFAULT_PORTAL_CONFIG.cache, ...config.cache },
    ui: { ...DEFAULT_PORTAL_CONFIG.ui, ...config.ui },
    roleLabels: { ...DEFAULT_PORTAL_CONFIG.roleLabels, ...config.roleLabels },
  };
}

/** Reset to defaults — intended for test isolation. */
export function resetRuntimeConfig(): void {
  current = DEFAULT_PORTAL_CONFIG;
}
