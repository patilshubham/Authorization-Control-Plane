// Centralized React Query keys. One source of truth for cache identity + invalidation.

export const qk = {
  config: ["config"] as const,
  tenants: ["tenants"] as const,
  tenantDetail: (tenantId: string) => ["tenant-detail", tenantId] as const,
  applications: ["applications"] as const,
  applicationsFiltered: (filters: unknown) =>
    ["applications", "filtered", filters] as const,
  overview: ["overview"] as const,
  appOverview: (appId: string) => ["app-overview", appId] as const,
  users: ["users"] as const,
  userDetail: (email: string) => ["user-detail", email] as const,
  roles: (appId: string) => ["roles", appId] as const,
  permissions: (appId: string) => ["permissions", appId] as const,
  mappings: (appId: string) => ["mappings", appId] as const,
  policies: (appId: string) => ["policies", appId] as const,
  referenceData: (appId: string) => ["reference-data", appId] as const,
  assignments: (appId: string) => ["assignments", appId] as const,
  oidc: (appId: string) => ["oidc", appId] as const,
  audit: (appId: string) => ["audit", appId] as const,
  globalAudit: ["audit", "global"] as const,
  configFindings: (appId: string) => ["config-findings", appId] as const,
  sodRules: (appId: string) => ["sod-rules", appId] as const,
  sodViolations: (appId: string) => ["sod-violations", appId] as const,
  aiUsage: (windowDays: number) => ["ai-usage", windowDays] as const,
  aiPromptLogs: (query: {
    windowDays?: number;
    feature?: string;
    outcome?: string;
    failuresOnly?: boolean;
    page?: number;
    pageSize?: number;
  }) =>
    [
      "ai-prompt-logs",
      query.windowDays ?? 0,
      query.feature ?? "",
      query.outcome ?? "",
      query.failuresOnly ?? false,
      query.page ?? 1,
      query.pageSize ?? 25,
    ] as const,

  // Server-paginated / derived list keys. Kept as extensions of the base keys
  // above so the base-key prefix invalidation (e.g. qk.assignments(appId)) still
  // refreshes these. Arrays must stay byte-identical to their previous inline form.
  applicationsFilteredPaged: (filters: unknown) =>
    ["applications", "filtered", "paged", filters] as const,
  usersPaged: (opts: unknown) => ["users", "paged", opts] as const,
  auditFeed: (opts: unknown) => ["audit", "feed", opts] as const,
  auditSummary: (opts?: { appId?: string; days?: number }) =>
    ["audit", "summary", opts?.appId ?? null, opts?.days ?? null] as const,
  rolesPaged: (appId: string, opts: unknown) =>
    ["roles", appId, "paged", opts] as const,
  permissionsPaged: (appId: string, opts: unknown) =>
    ["permissions", appId, "paged", opts] as const,
  policiesPaged: (appId: string, opts: unknown) =>
    ["policies", appId, "paged", opts] as const,
  referenceDataPaged: (appId: string, opts: unknown) =>
    ["reference-data", appId, "paged", opts] as const,
  assignmentsPaged: (appId: string, opts: unknown) =>
    ["assignments", appId, "paged", opts] as const,
  assignmentsSummary: (appId: string) =>
    ["assignments", appId, "summary"] as const,
  policyHistory: (appId: string, policyKey?: string) =>
    policyKey === undefined
      ? (["policy-history", appId] as const)
      : (["policy-history", appId, policyKey] as const),
  decisionAnalytics: (appId: string, windowDays: number) =>
    ["decision-analytics", appId, windowDays] as const,
  reviewCampaigns: (appId: string) => ["review-campaigns", appId] as const,
  reviewCampaign: (appId: string, campaignId?: string | null) =>
    campaignId === undefined
      ? (["review-campaign", appId] as const)
      : (["review-campaign", appId, campaignId] as const),
};
