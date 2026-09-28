import type {
  ApplicationOverview,
  ApplicationSummary,
  AssignmentSummary,
  AuditActivitySummary,
  AuditEventSummary,
  OidcProviderSummary,
  PermissionSummary,
  PlatformOverview,
  PolicySummary,
  PortalConfig,
  ReferenceDataSummary,
  RolePermissionSummary,
  RoleSummary,
  SimulatorDecision,
  TenantDetail,
  TenantSummary,
  UserAccess,
  UserDirectoryEntry,
} from "./types";

import { getAccessToken } from "./auth";
import { getRuntimeConfig } from "./api/runtimeConfig";

export type { SimulatorDecision };

// ── Error types ───────────────────────────────────────────────────────────────

export type ApiErrorCode =
  | "UNAUTHORIZED"
  | "FORBIDDEN"
  | "CONFLICT"
  | "VALIDATION"
  | "SERVER_ERROR"
  | "NETWORK";

export class PortalApiError extends Error {
  readonly code: ApiErrorCode;
  readonly status?: number;
  readonly details?: Record<string, string>;

  constructor(
    message: string,
    code: ApiErrorCode,
    status?: number,
    details?: Record<string, string>,
  ) {
    super(message);
    this.code = code;
    this.status = status;
    this.details = details;
  }
}

// ── Input types ───────────────────────────────────────────────────────────────

export type CreateApplicationInput = {
  applicationId: string;
  name: string;
  tenantId: string;
  riskLevel?: string;
  description?: string;
  combiningAlgorithm?: string;
};

export type UpdateApplicationInput = {
  applicationId: string;
  name: string;
  description?: string;
  tenantId: string;
  ownerTeam?: string;
  businessOwner?: string;
  technicalOwner?: string;
  riskLevel: string;
  combiningAlgorithm?: string;
};

export type CreateTenantInput = {
  tenantId: string;
  name: string;
  description?: string;
};

export type UpdateTenantInput = {
  tenantId: string;
  name: string;
  description?: string;
  status?: string;
};

export type CreateRoleInput = {
  roleKey: string;
  name: string;
  privileged: boolean;
  riskLevel: string;
  description?: string;
};

export type UpdateRoleInput = {
  roleKey: string;
  name: string;
  privileged: boolean;
  riskLevel: string;
  description?: string;
};

export type CreatePermissionInput = {
  permissionKey: string;
  resource: string;
  action: string;
  riskLevel: string;
  description?: string;
};

export type UpdatePermissionInput = {
  permissionKey: string;
  riskLevel: string;
  description?: string;
};

export type CreateRolePermissionInput = {
  roleKey: string;
  permissionKey: string;
  publish?: boolean;
};

export type CreateAssignmentInput = {
  subjectEmail: string;
  roleKey: string;
  validUntil: string | null;
};

export type UpdateAssignmentInput = {
  assignmentId: string;
  roleKey: string;
  validUntil: string | null;
  reason?: string;
};

export type CreatePolicyInput = {
  policyKey: string;
  permissionKey: string;
  effect: "ALLOW" | "DENY";
  conditions: string;
  priority?: number;
  obligations?: string;
  publish?: boolean;
};

export type UpdatePolicyInput = {
  policyKey: string;
  effect: "ALLOW" | "DENY";
  conditions: string;
  priority?: number;
  obligations?: string;
  publish?: boolean;
};

export type CreateReferenceDataInput = {
  key: string;
  description?: string;
  value: string;
};

export type UpdateReferenceDataInput = {
  key: string;
  description?: string;
  value: string;
};

export type CreateOidcProviderInput = {
  issuer: string;
  audience: string;
  jwksUri: string;
  allowedAlgorithms: string[];
  subjectType: string;
  subjectClaim: string;
};

export type UpdateOidcProviderInput = {
  id: string;
  issuer: string;
  audience: string;
  jwksUri: string;
  allowedAlgorithms: string[];
  subjectType: string;
  subjectClaim: string;
  enabled: boolean;
};

export type OidcProviderValidationInput = {
  issuer: string;
  audience: string;
  jwksUri: string;
  allowedAlgorithms: string[];
  subjectClaim: string;
};

export type OidcProviderCheck = {
  label: string;
  status: "PASS" | "WARN" | "FAIL";
  detail: string;
};

export type OidcProviderValidation = {
  ok: boolean;
  checks: OidcProviderCheck[];
};

export type PolicyHistoryEntry = {
  eventType: string;
  actor: string;
  actorRole: string | null;
  timestamp: string;
  oldValue: string | null;
  newValue: string | null;
};

export type PolicyHistory = {
  entries: PolicyHistoryEntry[];
};

export type DecisionCount = { label: string; count: number };

export type DecisionDay = { date: string; allowed: number; denied: number };

export type DecisionAnalytics = {
  windowDays: number;
  total: number;
  allowed: number;
  denied: number;
  topDenyReasons: DecisionCount[];
  topDeniedResources: DecisionCount[];
  daily: DecisionDay[];
};

export type AssignmentImportRowInput = {
  subjectEmail: string;
  roleKey: string;
  validUntil?: string | null;
};

export type AssignmentImportRequestBody = {
  dryRun: boolean;
  rows: AssignmentImportRowInput[];
};

export type AssignmentImportResultRow = {
  row: number;
  subjectEmail: string;
  roleKey: string;
  status: "CREATE" | "UPDATE" | "SKIP" | "ERROR";
  message: string | null;
};

export type AssignmentImportResult = {
  dryRun: boolean;
  total: number;
  valid: number;
  applied: number;
  failed: number;
  created: number;
  updated: number;
  skipped: number;
  results: AssignmentImportResultRow[];
};

export type ReviewCampaign = {
  id: string;
  name: string;
  status: string;
  dueAt: string | null;
  itemCount: number;
  pendingCount: number;
  keepCount: number;
  revokeCount: number;
  createdAt: string;
};

export type ReviewItem = {
  id: string;
  subjectEmail: string;
  roleKey: string;
  decision: string;
  decisionNote: string | null;
  decidedAt: string | null;
  decidedBy: string | null;
};

export type ReviewCampaignDetail = {
  campaign: ReviewCampaign;
  items: ReviewItem[];
};

export type SimulatorInput = {
  applicationId: string;
  subjectEmail: string;
  resourceType: string;
  resourceId: string;
  action: string;
  context: Record<string, unknown>;
};

export type PolicyDraftInput = {
  applicationId: string;
  instruction: string;
};

export type PolicyDraftResult = {
  conditionsJson: string;
  summary: string;
  warnings: string[];
  suggestedEffect: "ALLOW" | "DENY" | null;
};

export type ExplainDecisionInput = {
  applicationId: string;
  allowed: boolean;
  denyReason?: string | null;
  subjectType?: string;
  subjectEmail?: string | null;
  resourceType: string;
  resourceId?: string | null;
  action: string;
  matchedRoles: string[];
  matchedPermissions: string[];
  matchedPolicies: string[];
  contextKeys?: string[];
};

export type DecisionExplanation = {
  narrative: string;
  remediation: string[];
};

export type ImpactAnalysisInput = {
  applicationId: string;
  policyKey: string;
};

export type ImpactFlip = {
  subjectEmail: string | null;
  resourceId: string | null;
  action: string;
  before: boolean;
  after: boolean;
  reason: string | null;
};

export type ImpactAnalysis = {
  policyKey: string;
  effect: string;
  evaluatedCount: number;
  allowToDenyCount: number;
  denyToAllowCount: number;
  sampledFromHistory: boolean;
  summary: string;
  flips: ImpactFlip[];
};

export type ConfigFinding = {
  id: string;
  kind: string;
  severity: "HIGH" | "MEDIUM" | "LOW";
  title: string;
  detail: string;
  entityType: string | null;
  entityKey: string | null;
  suggestedFix: string | null;
};

export type ConfigAdvisorFindings = {
  findings: ConfigFinding[];
};

export type ConfigAdvisorSummary = {
  summary: string;
  findings: ConfigFinding[];
};

export type AccessSearchResult = {
  applicationId: string;
  entityType: string;
  title: string;
  detail: string;
  deepLinkKind:
    | "role"
    | "permission"
    | "policy"
    | "user"
    | "application"
    | "tenant"
    | "reviewCampaign"
    | null;
  deepLinkKey: string | null;
  children?: AccessSearchResult[] | null;
  applicationName?: string;
  tenantName?: string;
};

export type AccessSearchFilter = {
  field: string;
  operator: string;
  value: string;
};

export type AccessSearchResultMode =
  | "records"
  | "count"
  | "group"
  | "tree"
  | "guidance";

export type AccessSearchGuidance = {
  reason: string;
  intents: string[];
  suggestions: string[];
};

export type AccessSearchResponse = {
  entity: string;
  explanation: string | null;
  filters: AccessSearchFilter[];
  results: AccessSearchResult[];
  mode: AccessSearchResultMode;
  guidance?: AccessSearchGuidance | null;
};

export type SodMatcher = {
  permissionKey: string | null;
  resource: string | null;
  action: string | null;
};

export type SodRule = {
  ruleKey: string;
  name: string;
  rationale: string | null;
  severity: string;
  matcherA: SodMatcher;
  matcherB: SodMatcher;
  status: string;
};

export type SodRulesResponse = {
  rules: SodRule[];
};

export type SodViolation = {
  ruleKey: string;
  ruleName: string;
  severity: string;
  rationale: string | null;
  scope: "ROLE" | "SUBJECT";
  subjectKey: string;
  subjectLabel: string;
  conflictingPermissions: string[];
  detail: string;
  deepLinkKind: "role" | "user" | null;
  deepLinkKey: string | null;
};

export type SodViolationsResponse = {
  violations: SodViolation[];
};

export type AccessReviewItem = {
  id: string;
  applicationId: string;
  roleKey: string;
  roleName: string;
  privileged: boolean;
  riskLevel: string;
  status: string;
  validFrom: string;
  validUntil: string | null;
  source: string;
  reason: string | null;
  lastUsedAt: string | null;
  lastUsedDaysAgo: number | null;
  dormant: boolean;
  peerCount: number;
  permissions: string[];
  recommendation: "KEEP" | "REVOKE" | "REVIEW";
  recommendationReason: string;
  rationale: string | null;
};

export type AccessReviewResponse = {
  subjectEmail: string;
  summary: string | null;
  items: AccessReviewItem[];
};

export type AuditNarrativeEvent = {
  eventId: string;
  eventType: string;
  applicationId: string | null;
  actorEmail: string | null;
  targetSubjectEmail: string | null;
  timestamp: string;
  oldValue: string | null;
  newValue: string | null;
  reason: string | null;
};

export type AuditNarrativeSection = {
  heading: string;
  detail: string;
  eventIds: string[];
};

export type AuditGroupCount = {
  key: string;
  count: number;
};

export type AuditDenyReason = {
  applicationId: string;
  denyReason: string;
  count: number;
};

export type AuditNarrativeResponse = {
  fromUtc: string;
  toUtc: string;
  totalEvents: number;
  summary: string | null;
  sections: AuditNarrativeSection[];
  events: AuditNarrativeEvent[];
  byApplication: AuditGroupCount[];
  byEventType: AuditGroupCount[];
  topDenyReasons: AuditDenyReason[];
};

export type AuditNarrativeInput = {
  fromUtc?: string;
  toUtc?: string;
  applicationId?: string;
  actorEmail?: string;
  eventType?: string;
};

export type AiUsageFeatureCount = {
  feature: string;
  count: number;
  successCount: number;
  totalTokens: number;
};

export type AiUsageActorCount = {
  actor: string;
  count: number;
};

export type AiUsageApplicationCount = {
  applicationId: string;
  count: number;
};

export type AiUsageDailyCount = {
  date: string;
  count: number;
};

export type AiUsageReport = {
  fromUtc: string;
  toUtc: string;
  totalInvocations: number;
  successCount: number;
  timeoutCount: number;
  errorCount: number;
  promptTokens: number;
  completionTokens: number;
  totalTokens: number;
  estimatedCostUsd: number | null;
  p50LatencyMs: number;
  p95LatencyMs: number;
  byFeature: AiUsageFeatureCount[];
  byActor: AiUsageActorCount[];
  byApplication: AiUsageApplicationCount[];
  trend: AiUsageDailyCount[];
};

export type AiUsageResponse = {
  windowDays: number;
  report: AiUsageReport;
};

export type AiPromptLogItem = {
  id: string;
  feature: string;
  actorEmail: string | null;
  actorRole: string | null;
  applicationId: string | null;
  promptText: string;
  outcome: string;
  errorMessage: string | null;
  interpretation: string | null;
  provider: string;
  model: string | null;
  timestamp: string;
  correlationId: string | null;
};

export type AiPromptLogResponse = {
  windowDays: number;
  page: number;
  pageSize: number;
  total: number;
  items: AiPromptLogItem[];
};

export type AiPromptLogQuery = {
  windowDays?: number;
  feature?: string;
  outcome?: string;
  failuresOnly?: boolean;
  page?: number;
  pageSize?: number;
};

export type SodRuleDraft = {
  name: string;
  rationale: string;
  severity: string;
  matcherA: SodMatcher;
  matcherB: SodMatcher;
  warnings: string[];
};

export type SodRuleSaveInput = {
  ruleKey: string;
  name: string;
  rationale?: string;
  severity: string;
  matcherA: SodMatcher;
  matcherB: SodMatcher;
};

export type ApplicationFilters = {
  tenantId?: string;
  status?: string;
  riskLevel?: string;
  q?: string;
  page?: number;
  pageSize?: number;
};

/** Server-side pagination envelope returned by list endpoints. `total` is the
 * full match count across all pages so the UI can render "N of M". */
export type Paged<T> = {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
};

/** Raw (pre-mapping) paged envelope as it arrives over the wire. */
type ApiPaged<T> = {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
};

/** Optional page/pageSize request parameters for a paged list call. */
export type PageParams = { page?: number; pageSize?: number };

/** Aggregate assignment stats used to drive charts without pulling every row. */
export type AssignmentStats = {
  total: number;
  states: { state: string; count: number }[];
};


// ── HTTP core ─────────────────────────────────────────────────────────────────

/**
 * Resolves the API base URL. In production the `VITE_API_BASE_URL` build-time
 * variable is required — we fail fast rather than silently falling back to a
 * localhost origin that would never work for real users. The localhost default
 * is a developer convenience only (dev server / test runs).
 */
function resolveApiBaseUrl(): string {
  const configured = import.meta.env.VITE_API_BASE_URL;
  if (configured) return configured;
  if (import.meta.env.DEV) return "http://localhost:8080";
  throw new Error(
    "VITE_API_BASE_URL is not configured. Set it at build time for production deployments.",
  );
}

const apiBaseUrl = resolveApiBaseUrl();

/** Builds a `?page=&pageSize=` query string (plus optional extra params),
 * omitting anything not supplied. Returns "" when there is nothing to send. */
function pageQuery(
  page?: PageParams,
  extra?: Record<string, string>,
): string {
  const params = new URLSearchParams();
  if (page?.page != null) params.set("page", String(page.page));
  if (page?.pageSize != null) params.set("pageSize", String(page.pageSize));
  for (const [k, v] of Object.entries(extra ?? {})) params.set(k, v);
  const query = params.toString();
  return query ? `?${query}` : "";
}


async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const accessToken = await getAccessToken();
  try {
    const response = await fetch(`${apiBaseUrl}${path}`, {
      ...init,
      headers: {
        "Content-Type": "application/json",
        "X-Correlation-ID": crypto.randomUUID(),
        ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
        ...init?.headers,
      },
    });

    if (!response.ok) {
      throw await mapError(response);
    }

    if (response.status === 204) {
      return undefined as T;
    }

    return (await response.json()) as T;
  } catch (error) {
    if (error instanceof PortalApiError) {
      throw error;
    }
    throw new PortalApiError(
      "The API is not reachable. Check that the backend is running.",
      "NETWORK",
    );
  }
}

async function mapError(response: Response): Promise<PortalApiError> {
  const fallback = response.statusText || "Request failed";
  let body:
    | {
        error?: {
          code?: string;
          message?: string;
          details?: Record<string, string>;
        };
      }
    | undefined;
  try {
    body = (await response.json()) as typeof body;
  } catch {
    body = undefined;
  }

  if (response.status === 401) {
    return new PortalApiError(
      body?.error?.message ?? "Sign in again to continue.",
      "UNAUTHORIZED",
      response.status,
    );
  }
  if (response.status === 403) {
    return new PortalApiError(
      body?.error?.message ?? "You do not have access to this action.",
      "FORBIDDEN",
      response.status,
    );
  }
  if (response.status === 409) {
    return new PortalApiError(
      body?.error?.message ?? "A record with this identifier already exists.",
      "CONFLICT",
      response.status,
    );
  }
  if (response.status === 422 || response.status === 400) {
    return new PortalApiError(
      body?.error?.message ?? fallback,
      "VALIDATION",
      response.status,
      body?.error?.details,
    );
  }
  return new PortalApiError(
    body?.error?.message ?? fallback,
    "SERVER_ERROR",
    response.status,
  );
}

// ── Backend entity shapes ─────────────────────────────────────────────────────

type ApiAssignment = {
  id: string;
  subjectEmail: string | null;
  roleKey: string;
  state: string;
  validUntil: string | null;
  reason: string | null;
};

type ApiPermission = {
  id: string;
  permissionKey: string;
  resource: string;
  action: string;
  riskLevel: string;
  status: string;
  description?: string | null;
};

type ApiRolePermission = {
  id: string;
  roleKey: string;
  permissionKey: string;
  state: string;
  publishedAt: string | null;
};

type ApiPolicy = {
  policyKey: string;
  permissionKey: string;
  effect: string;
  state: string;
  priority: number;
  obligations: string;
  publishedAt: string | null;
  conditions: string;
};

type ApiReferenceData = {
  id: string;
  key: string;
  description: string | null;
  value: string;
  status: string;
  createdAt: string | null;
  createdBy: string | null;
  updatedAt: string | null;
};

type ApiOidcProvider = {
  id: string;
  providerType: string;
  issuer: string;
  audience: string;
  jwksUri: string;
  allowedAlgorithms: string[];
  subjectType: string;
  subjectClaim: string;
  enabled: boolean;
  createdAt: string | null;
  createdBy: string | null;
  updatedAt: string | null;
};

type ApiAuditEvent = {
  eventId: string;
  eventType: string;
  applicationId: string | null;
  actorEmail: string | null;
  actorRole: string | null;
  targetSubjectEmail: string | null;
  timestamp: string;
  oldValue: string | null;
  newValue: string | null;
  correlationId: string | null;
};

type ApiPlatformOverview = Omit<PlatformOverview, "recentAudit"> & {
  recentAudit: ApiAuditEvent[];
};
type ApiApplicationOverview = Omit<ApplicationOverview, "recentAudit"> & {
  recentAudit: ApiAuditEvent[];
};

// ── Mappers ───────────────────────────────────────────────────────────────────

function mapAssignment(a: ApiAssignment): AssignmentSummary {
  return {
    id: a.id,
    subjectEmail: a.subjectEmail ?? "",
    roleKey: a.roleKey,
    state: a.state,
    validUntil: a.validUntil,
    reason: a.reason ?? null,
  };
}

function mapPermission(p: ApiPermission): PermissionSummary {
  return {
    id: p.id,
    permissionKey: p.permissionKey,
    resource: p.resource,
    action: p.action,
    riskLevel: p.riskLevel,
    status: p.status,
    description: p.description ?? null,
  };
}

function mapRolePermission(m: ApiRolePermission): RolePermissionSummary {
  return {
    id: m.id,
    roleKey: m.roleKey,
    permissionKey: m.permissionKey,
    state: m.state,
    publishedAt: m.publishedAt
      ? new Date(m.publishedAt).toLocaleDateString()
      : null,
  };
}

function mapPolicy(p: ApiPolicy): PolicySummary {
  return {
    policyKey: p.policyKey,
    permissionKey: p.permissionKey,
    effect: p.effect,
    state: p.state,
    priority: p.priority ?? 0,
    obligations: p.obligations ?? "[]",
    publishedAt: p.publishedAt
      ? new Date(p.publishedAt).toLocaleDateString()
      : null,
    conditions: p.conditions,
  };
}

function mapReferenceData(r: ApiReferenceData): ReferenceDataSummary {
  return {
    id: r.id,
    key: r.key,
    description: r.description ?? null,
    value: r.value,
    status: r.status,
    createdAt: r.createdAt ?? null,
    createdBy: r.createdBy ?? null,
    updatedAt: r.updatedAt ?? null,
  };
}

function mapOidcProvider(o: ApiOidcProvider): OidcProviderSummary {
  return {
    id: o.id,
    providerType: o.providerType ?? "OIDC",
    issuer: o.issuer,
    audience: o.audience,
    jwksUri: o.jwksUri,
    allowedAlgorithms: o.allowedAlgorithms,
    subjectType: o.subjectType ?? "USER",
    subjectClaim: o.subjectClaim ?? "sub",
    enabled: o.enabled,
    createdAt: o.createdAt ?? null,
    createdBy: o.createdBy ?? null,
    updatedAt: o.updatedAt ?? null,
  };
}

function mapAuditEvent(e: ApiAuditEvent): AuditEventSummary {
  return {
    eventId: e.eventId,
    eventType: e.eventType,
    applicationId: e.applicationId ?? "",
    actorEmail: e.actorEmail ?? "",
    actorRole: e.actorRole,
    targetSubjectEmail: e.targetSubjectEmail,
    timestamp: e.timestamp,
    oldValue: e.oldValue,
    newValue: e.newValue,
    correlationId: e.correlationId,
  };
}

// ── Portal API ────────────────────────────────────────────────────────────────

export const portalApi = {
  // Server-owned runtime configuration (feature flags such as AI).
  async getConfig(): Promise<PortalConfig> {
    return request<PortalConfig>("/v1/config");
  },
  // Tenants
  async listTenants(): Promise<TenantSummary[]> {
    return request<TenantSummary[]>("/v1/admin/tenants");
  },
  async createTenant(input: CreateTenantInput): Promise<TenantSummary> {
    return request<TenantSummary>("/v1/admin/tenants", {
      method: "POST",
      body: JSON.stringify(input),
    });
  },
  async updateTenant(input: UpdateTenantInput): Promise<TenantSummary> {
    return request<TenantSummary>(
      `/v1/admin/tenants/${encodeURIComponent(input.tenantId)}`,
      {
        method: "PUT",
        body: JSON.stringify({
          name: input.name,
          description: input.description,
          status: input.status,
        }),
      },
    );
  },

  async deleteTenant(tenantId: string): Promise<void> {
    return request<void>(`/v1/admin/tenants/${encodeURIComponent(tenantId)}`, {
      method: "DELETE",
    });
  },

  // Applications
  async listApplications(
    filters?: ApplicationFilters,
  ): Promise<ApplicationSummary[]> {
    return (await portalApi.listApplicationsPaged(filters)).items;
  },
  async listApplicationsPaged(
    filters?: ApplicationFilters,
  ): Promise<Paged<ApplicationSummary>> {
    const params = new URLSearchParams();
    if (filters?.tenantId) params.set("tenantId", filters.tenantId);
    if (filters?.status) params.set("status", filters.status);
    if (filters?.riskLevel) params.set("riskLevel", filters.riskLevel);
    if (filters?.q) params.set("q", filters.q);
    if (filters?.page != null) params.set("page", String(filters.page));
    if (filters?.pageSize != null)
      params.set("pageSize", String(filters.pageSize));
    const query = params.toString();
    return request<Paged<ApplicationSummary>>(
      `/v1/admin/applications${query ? `?${query}` : ""}`,
    );
  },
  async createApplication(
    input: CreateApplicationInput,
  ): Promise<ApplicationSummary> {
    return request<ApplicationSummary>("/v1/admin/applications", {
      method: "POST",
      body: JSON.stringify({
        ...input,
        tenantId: input.tenantId,
        riskLevel: input.riskLevel ?? "MEDIUM",
        policyCombiningAlgorithm: input.combiningAlgorithm ?? "deny-overrides",
      }),
    });
  },
  async updateApplication(
    input: UpdateApplicationInput,
  ): Promise<ApplicationSummary> {
    return request<ApplicationSummary>(
      `/v1/admin/applications/${encodeURIComponent(input.applicationId)}`,
      {
        method: "PUT",
        body: JSON.stringify({
          name: input.name,
          description: input.description || undefined,
          tenantId: input.tenantId,
          ownerTeam: input.ownerTeam || undefined,
          businessOwner: input.businessOwner || undefined,
          technicalOwner: input.technicalOwner || undefined,
          riskLevel: input.riskLevel,
          policyCombiningAlgorithm: input.combiningAlgorithm || undefined,
        }),
      },
    );
  },
  async disableApplication(applicationId: string): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/disable`,
      { method: "POST" },
    );
  },
  async archiveApplication(applicationId: string): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/archive`,
      { method: "POST" },
    );
  },
  async activateApplication(applicationId: string): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/activate`,
      { method: "POST" },
    );
  },

  // OIDC Providers
  async listOidcProviders(
    applicationId: string,
  ): Promise<OidcProviderSummary[]> {
    const data = await request<ApiOidcProvider[]>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/oidc-providers`,
    );
    return data.map(mapOidcProvider);
  },
  async createOidcProvider(
    applicationId: string,
    input: CreateOidcProviderInput,
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/oidc-providers`,
      {
        method: "POST",
        body: JSON.stringify({
          ...input,
          allowedAlgorithms: input.allowedAlgorithms.length
            ? input.allowedAlgorithms
            : ["RS256"],
        }),
      },
    );
  },
  async updateOidcProvider(
    applicationId: string,
    input: UpdateOidcProviderInput,
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/oidc-providers/${encodeURIComponent(input.id)}`,
      {
        method: "PUT",
        body: JSON.stringify({
          issuer: input.issuer,
          audience: input.audience,
          jwksUri: input.jwksUri,
          allowedAlgorithms: input.allowedAlgorithms.length
            ? input.allowedAlgorithms
            : ["RS256"],
          subjectType: input.subjectType,
          subjectClaim: input.subjectClaim,
          enabled: input.enabled,
        }),
      },
    );
  },
  async validateOidcProvider(
    applicationId: string,
    input: OidcProviderValidationInput,
  ): Promise<OidcProviderValidation> {
    return request<OidcProviderValidation>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/oidc-providers/validate`,
      {
        method: "POST",
        body: JSON.stringify({
          ...input,
          allowedAlgorithms: input.allowedAlgorithms.length
            ? input.allowedAlgorithms
            : ["RS256"],
        }),
      },
    );
  },

  // Roles
  async listRoles(applicationId: string): Promise<RoleSummary[]> {
    return (await portalApi.listRolesPaged(applicationId)).items;
  },
  async listRolesPaged(
    applicationId: string,
    opts?: PageParams & { q?: string; status?: string; privileged?: boolean },
  ): Promise<Paged<RoleSummary>> {
    const extra: Record<string, string> = {};
    if (opts?.q) extra.q = opts.q;
    if (opts?.status) extra.status = opts.status;
    if (opts?.privileged != null) extra.privileged = String(opts.privileged);
    return request<Paged<RoleSummary>>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/roles${pageQuery(opts, extra)}`,
    );
  },
  async createRole(
    applicationId: string,
    input: CreateRoleInput,
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/roles`,
      {
        method: "POST",
        body: JSON.stringify(input),
      },
    );
  },
  async updateRole(
    applicationId: string,
    input: UpdateRoleInput,
  ): Promise<void> {
    const { roleKey, ...body } = input;
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/roles/${encodeURIComponent(roleKey)}`,
      {
        method: "PUT",
        body: JSON.stringify(body),
      },
    );
  },
  async deleteRole(applicationId: string, roleKey: string): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/roles/${encodeURIComponent(roleKey)}`,
      { method: "DELETE" },
    );
  },
  async setRoleStatus(
    applicationId: string,
    roleKey: string,
    action: "activate" | "disable" | "archive",
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/roles/${encodeURIComponent(roleKey)}/${action}`,
      { method: "POST" },
    );
  },

  // Permissions
  async listPermissions(applicationId: string): Promise<PermissionSummary[]> {
    return (await portalApi.listPermissionsPaged(applicationId)).items;
  },
  async listPermissionsPaged(
    applicationId: string,
    opts?: PageParams & { q?: string; status?: string },
  ): Promise<Paged<PermissionSummary>> {
    const extra: Record<string, string> = {};
    if (opts?.q) extra.q = opts.q;
    if (opts?.status) extra.status = opts.status;
    const data = await request<ApiPaged<ApiPermission>>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/permissions${pageQuery(opts, extra)}`,
    );
    return { ...data, items: data.items.map(mapPermission) };
  },
  async createPermission(
    applicationId: string,
    input: CreatePermissionInput,
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/permissions`,
      {
        method: "POST",
        body: JSON.stringify(input),
      },
    );
  },
  async updatePermission(
    applicationId: string,
    input: UpdatePermissionInput,
  ): Promise<void> {
    const { permissionKey, ...body } = input;
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/permissions/${encodeURIComponent(permissionKey)}`,
      {
        method: "PUT",
        body: JSON.stringify(body),
      },
    );
  },
  async deletePermission(
    applicationId: string,
    permissionKey: string,
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/permissions/${encodeURIComponent(permissionKey)}`,
      { method: "DELETE" },
    );
  },
  async setPermissionStatus(
    applicationId: string,
    permissionKey: string,
    action: "activate" | "disable" | "archive",
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/permissions/${encodeURIComponent(permissionKey)}/${action}`,
      { method: "POST" },
    );
  },

  // Role-Permission Mappings
  async listRoleMappings(
    applicationId: string,
  ): Promise<RolePermissionSummary[]> {
    const data = await request<ApiRolePermission[]>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/role-permissions`,
    );
    return data.map(mapRolePermission);
  },
  async createRolePermission(
    applicationId: string,
    input: CreateRolePermissionInput,
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/role-permissions`,
      {
        method: "POST",
        body: JSON.stringify({ ...input, publish: input.publish ?? false }),
      },
    );
  },
  async publishRoleMapping(
    applicationId: string,
    rolePermissionId: string,
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/role-permissions/${encodeURIComponent(rolePermissionId)}/publish`,
      { method: "POST" },
    );
  },
  async unmapRolePermission(
    applicationId: string,
    rolePermissionId: string,
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/role-permissions/${encodeURIComponent(rolePermissionId)}`,
      { method: "DELETE" },
    );
  },

  // Assignments
  async listAssignments(applicationId: string): Promise<AssignmentSummary[]> {
    return (await portalApi.listAssignmentsPaged(applicationId)).items;
  },
  async listAssignmentsPaged(
    applicationId: string,
    opts?: PageParams & { q?: string; state?: string; expiry?: string },
  ): Promise<Paged<AssignmentSummary>> {
    const extra: Record<string, string> = {};
    if (opts?.q) extra.q = opts.q;
    if (opts?.state) extra.state = opts.state;
    if (opts?.expiry) extra.expiry = opts.expiry;
    const data = await request<ApiPaged<ApiAssignment>>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/assignments${pageQuery(opts, extra)}`,
    );
    return { ...data, items: data.items.map(mapAssignment) };
  },
  async getAssignmentsSummary(
    applicationId: string,
    state?: string,
  ): Promise<AssignmentStats> {
    return request<AssignmentStats>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/assignments/summary${state ? `?state=${encodeURIComponent(state)}` : ""}`,
    );
  },
  async createAssignment(
    applicationId: string,
    input: CreateAssignmentInput,
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/assignments`,
      {
        method: "POST",
        body: JSON.stringify({
          subjectType: "USER",
          subjectEmail: input.subjectEmail,
          roleKey: input.roleKey,
          validUntil: input.validUntil,
        }),
      },
    );
  },
  async revokeAssignment(
    applicationId: string,
    assignmentId: string,
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/assignments/${assignmentId}/revoke`,
      { method: "POST" },
    );
  },
  async updateAssignmentExpiry(
    applicationId: string,
    assignmentId: string,
    validUntil: string,
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/assignments/${assignmentId}/extend`,
      {
        method: "POST",
        body: JSON.stringify({ validUntil }),
      },
    );
  },
  async updateAssignment(
    applicationId: string,
    input: UpdateAssignmentInput,
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/assignments/${input.assignmentId}`,
      {
        method: "PUT",
        body: JSON.stringify({
          roleKey: input.roleKey,
          validUntil: input.validUntil,
          reason: input.reason || undefined,
        }),
      },
    );
  },
  async breakGlass(
    applicationId: string,
    input: {
      subjectEmail: string;
      roleKey: string;
      reason: string;
      durationHours: number;
    },
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/assignments/break-glass`,
      { method: "POST", body: JSON.stringify(input) },
    );
  },
  // Streams the application's assignments as a CSV file and triggers a browser download.
  // Resolves with the downloaded file name (honouring the server's Content-Disposition) so the
  // caller can confirm the download to the user.
  async exportAssignmentsCsv(applicationId: string): Promise<string> {
    const accessToken = await getAccessToken();
    const response = await fetch(
      `${apiBaseUrl}/v1/admin/applications/${encodeURIComponent(applicationId)}/assignments/export`,
      {
        headers: {
          "X-Correlation-ID": crypto.randomUUID(),
          ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
        },
      },
    );
    if (!response.ok) {
      throw await mapError(response);
    }
    const disposition = response.headers.get("Content-Disposition") ?? "";
    const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
    const fileName = match
      ? decodeURIComponent(match[1])
      : `${applicationId}-assignments.csv`;
    const blob = await response.blob();
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
    return fileName;
  },
  async importAssignments(
    applicationId: string,
    input: AssignmentImportRequestBody,
  ): Promise<AssignmentImportResult> {
    return request<AssignmentImportResult>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/assignments/import`,
      { method: "POST", body: JSON.stringify(input) },
    );
  },

  // Policies
  async listPolicies(applicationId: string): Promise<PolicySummary[]> {
    return (await portalApi.listPoliciesPaged(applicationId)).items;
  },
  async listPoliciesPaged(
    applicationId: string,
    opts?: PageParams & { q?: string; effect?: string; state?: string },
  ): Promise<Paged<PolicySummary>> {
    const extra: Record<string, string> = {};
    if (opts?.q) extra.q = opts.q;
    if (opts?.effect) extra.effect = opts.effect;
    if (opts?.state) extra.state = opts.state;
    const data = await request<ApiPaged<ApiPolicy>>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/policies${pageQuery(opts, extra)}`,
    );
    return { ...data, items: data.items.map(mapPolicy) };
  },
  async createPolicy(
    applicationId: string,
    input: CreatePolicyInput,
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/policies`,
      {
        method: "POST",
        body: JSON.stringify(input),
      },
    );
  },
  async updatePolicy(
    applicationId: string,
    input: UpdatePolicyInput,
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/policies/${encodeURIComponent(input.policyKey)}`,
      {
        method: "PUT",
        body: JSON.stringify({
          effect: input.effect,
          conditions: input.conditions,
          priority: input.priority ?? 0,
          obligations: input.obligations ?? "[]",
          publish: input.publish ?? false,
        }),
      },
    );
  },
  async publishPolicy(applicationId: string, policyKey: string): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/policies/${encodeURIComponent(policyKey)}/publish`,
      { method: "POST" },
    );
  },
  async deletePolicy(applicationId: string, policyKey: string): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/policies/${encodeURIComponent(policyKey)}`,
      { method: "DELETE" },
    );
  },
  async getPolicyHistory(
    applicationId: string,
    policyKey: string,
  ): Promise<PolicyHistory> {
    return request<PolicyHistory>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/policies/${encodeURIComponent(policyKey)}/history`,
    );
  },
  async getDecisionAnalytics(
    applicationId: string,
    windowDays?: number,
  ): Promise<DecisionAnalytics> {
    const query =
      windowDays && windowDays > 0 ? `?windowDays=${windowDays}` : "";
    return request<DecisionAnalytics>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/decisions/analytics${query}`,
    );
  },
  // Certification campaigns
  async listReviewCampaigns(applicationId: string): Promise<ReviewCampaign[]> {
    return request<ReviewCampaign[]>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/review-campaigns`,
    );
  },
  async getReviewCampaign(
    applicationId: string,
    campaignId: string,
  ): Promise<ReviewCampaignDetail> {
    return request<ReviewCampaignDetail>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/review-campaigns/${encodeURIComponent(campaignId)}`,
    );
  },
  async createReviewCampaign(
    applicationId: string,
    input: { name: string; dueAt?: string | null },
  ): Promise<ReviewCampaign> {
    return request<ReviewCampaign>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/review-campaigns`,
      { method: "POST", body: JSON.stringify(input) },
    );
  },
  async activateReviewCampaign(
    applicationId: string,
    campaignId: string,
  ): Promise<ReviewCampaignDetail> {
    return request<ReviewCampaignDetail>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/review-campaigns/${encodeURIComponent(campaignId)}/activate`,
      { method: "POST" },
    );
  },
  async decideReviewItem(
    applicationId: string,
    campaignId: string,
    itemId: string,
    input: { decision: string; note?: string },
  ): Promise<ReviewItem> {
    return request<ReviewItem>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/review-campaigns/${encodeURIComponent(campaignId)}/items/${encodeURIComponent(itemId)}/decision`,
      { method: "POST", body: JSON.stringify(input) },
    );
  },
  // Applies one decision to many items at once. Omit itemIds to decide every still-pending item.
  async decideReviewItemsBulk(
    applicationId: string,
    campaignId: string,
    input: { decision: string; itemIds?: string[]; note?: string },
  ): Promise<ReviewCampaignDetail> {
    return request<ReviewCampaignDetail>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/review-campaigns/${encodeURIComponent(campaignId)}/decisions`,
      { method: "POST", body: JSON.stringify(input) },
    );
  },
  async finalizeReviewCampaign(
    applicationId: string,
    campaignId: string,
  ): Promise<ReviewCampaignDetail> {
    return request<ReviewCampaignDetail>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/review-campaigns/${encodeURIComponent(campaignId)}/finalize`,
      { method: "POST" },
    );
  },

  // Reference data
  async listReferenceData(
    applicationId: string,
  ): Promise<ReferenceDataSummary[]> {
    return (await portalApi.listReferenceDataPaged(applicationId)).items;
  },
  async listReferenceDataPaged(
    applicationId: string,
    opts?: PageParams & { q?: string; status?: string },
  ): Promise<Paged<ReferenceDataSummary>> {
    const extra: Record<string, string> = {};
    if (opts?.q) extra.q = opts.q;
    if (opts?.status) extra.status = opts.status;
    const data = await request<ApiPaged<ApiReferenceData>>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/reference-data${pageQuery(opts, extra)}`,
    );
    return { ...data, items: data.items.map(mapReferenceData) };
  },
  async createReferenceData(
    applicationId: string,
    input: CreateReferenceDataInput,
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/reference-data`,
      {
        method: "POST",
        body: JSON.stringify({
          key: input.key,
          description: input.description || undefined,
          value: input.value,
        }),
      },
    );
  },
  async updateReferenceData(
    applicationId: string,
    input: UpdateReferenceDataInput,
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/reference-data/${encodeURIComponent(input.key)}`,
      {
        method: "PUT",
        body: JSON.stringify({
          description: input.description || undefined,
          value: input.value,
        }),
      },
    );
  },
  async deleteReferenceData(
    applicationId: string,
    key: string,
  ): Promise<void> {
    return request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/reference-data/${encodeURIComponent(key)}`,
      { method: "DELETE" },
    );
  },

  // Audit
  async listAuditEvents(
    applicationId: string,
    filters?: { eventType?: string; actorEmail?: string },
  ): Promise<AuditEventSummary[]> {
    const params = new URLSearchParams({ applicationId });
    if (filters?.actorEmail) params.set("actorEmail", filters.actorEmail);
    // Request a generous bounded window so the activity feed and calendar
    // heatmap keep enough history (the endpoint is always server-paged).
    params.set("pageSize", String(getRuntimeConfig().ui.auditPageSize));
    const data = await request<ApiPaged<ApiAuditEvent>>(
      `/v1/admin/audit-events?${params.toString()}`,
    );
    const events = data.items.map(mapAuditEvent);
    // Client-side event-type filter (backend does not yet support it as a query param)
    if (filters?.eventType) {
      return events.filter((e) =>
        e.eventType.toLowerCase().includes(filters.eventType!.toLowerCase()),
      );
    }
    return events;
  },

  // Paged audit feed with server-side text search (`q`) and category filter.
  // `applicationId` scopes to a single app (per-app Activity view); omit it for
  // the global Audit view. The heatmap is fed separately by the summary below.
  async listAuditEventsPaged(
    opts?: PageParams & { applicationId?: string; q?: string; category?: string },
  ): Promise<Paged<AuditEventSummary>> {
    const extra: Record<string, string> = {};
    if (opts?.applicationId) extra.applicationId = opts.applicationId;
    if (opts?.q) extra.q = opts.q;
    if (opts?.category) extra.category = opts.category;
    const data = await request<ApiPaged<ApiAuditEvent>>(
      `/v1/admin/audit-events${pageQuery(opts, extra)}`,
    );
    return { ...data, items: data.items.map(mapAuditEvent) };
  },

  // Lightweight per-day activity counts for the calendar heatmap, so the feed
  // can paginate without the heatmap needing every event row.
  async getAuditActivitySummary(opts?: {
    applicationId?: string;
    days?: number;
  }): Promise<AuditActivitySummary> {
    const params = new URLSearchParams();
    if (opts?.applicationId) params.set("applicationId", opts.applicationId);
    if (opts?.days != null) params.set("days", String(opts.days));
    const query = params.toString();
    return request<AuditActivitySummary>(
      `/v1/admin/audit-events/summary${query ? `?${query}` : ""}`,
    );
  },

  // Overview / insights
  async getPlatformOverview(): Promise<PlatformOverview> {
    const data = await request<ApiPlatformOverview>("/v1/admin/overview");
    return { ...data, recentAudit: data.recentAudit.map(mapAuditEvent) };
  },
  async listGlobalAuditEvents(): Promise<AuditEventSummary[]> {
    const data = await request<ApiPaged<ApiAuditEvent>>(
      `/v1/admin/audit-events?pageSize=${getRuntimeConfig().ui.auditPageSize}`,
    );
    return data.items.map(mapAuditEvent);
  },
  async getApplicationOverview(
    applicationId: string,
  ): Promise<ApplicationOverview> {
    const data = await request<ApiApplicationOverview>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/overview`,
    );
    return { ...data, recentAudit: data.recentAudit.map(mapAuditEvent) };
  },
  async getTenantDetail(tenantId: string): Promise<TenantDetail> {
    return request<TenantDetail>(
      `/v1/admin/tenants/${encodeURIComponent(tenantId)}`,
    );
  },
  async listUsers(): Promise<UserDirectoryEntry[]> {
    return (await portalApi.listUsersPaged()).items;
  },
  async listUsersPaged(
    opts?: PageParams & { q?: string },
  ): Promise<Paged<UserDirectoryEntry>> {
    const extra: Record<string, string> = {};
    if (opts?.q) extra.q = opts.q;
    return request<Paged<UserDirectoryEntry>>(
      `/v1/admin/users${pageQuery(opts, extra)}`,
    );
  },
  async getUser(email: string): Promise<UserAccess> {
    return request<UserAccess>(`/v1/admin/users/${encodeURIComponent(email)}`);
  },

  // Simulator
  async simulate(input: SimulatorInput): Promise<SimulatorDecision> {
    return request<SimulatorDecision>("/v1/admin/simulator/authorize", {
      method: "POST",
      body: JSON.stringify({
        applicationId: input.applicationId,
        subjectType: "USER",
        subjectEmail: input.subjectEmail,
        resourceType: input.resourceType,
        resourceId: input.resourceId,
        action: input.action,
        context: input.context,
      }),
    });
  },

  // AI assistance (opt-in; endpoints return 404 when disabled)
  async draftPolicy(input: PolicyDraftInput): Promise<PolicyDraftResult> {
    return request<PolicyDraftResult>(
      `/v1/admin/applications/${encodeURIComponent(input.applicationId)}/ai/policy-draft`,
      {
        method: "POST",
        body: JSON.stringify({ instruction: input.instruction }),
      },
    );
  },
  async explainDecision(
    input: ExplainDecisionInput,
  ): Promise<DecisionExplanation> {
    return request<DecisionExplanation>(
      `/v1/admin/applications/${encodeURIComponent(input.applicationId)}/ai/explain-decision`,
      {
        method: "POST",
        body: JSON.stringify({
          allowed: input.allowed,
          denyReason: input.denyReason ?? null,
          subjectType: input.subjectType ?? "USER",
          subjectEmail: input.subjectEmail ?? null,
          resourceType: input.resourceType,
          resourceId: input.resourceId ?? null,
          action: input.action,
          matchedRoles: input.matchedRoles,
          matchedPermissions: input.matchedPermissions,
          matchedPolicies: input.matchedPolicies,
          contextKeys: input.contextKeys ?? [],
        }),
      },
    );
  },
  async analyzeImpact(input: ImpactAnalysisInput): Promise<ImpactAnalysis> {
    return request<ImpactAnalysis>(
      `/v1/admin/applications/${encodeURIComponent(input.applicationId)}/ai/impact-analysis`,
      {
        method: "POST",
        body: JSON.stringify({ policyKey: input.policyKey }),
      },
    );
  },
  async getConfigFindings(
    applicationId: string,
  ): Promise<ConfigAdvisorFindings> {
    // Deterministic signal — served by the AI-independent insights surface so the findings
    // render even when the AI subsystem is disabled. AI only ranks/explains them (summarize).
    return request<ConfigAdvisorFindings>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/insights/config-findings`,
    );
  },
  async summarizeConfigFindings(
    applicationId: string,
  ): Promise<ConfigAdvisorSummary> {
    return request<ConfigAdvisorSummary>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/ai/advisor/summarize`,
      { method: "POST" },
    );
  },
  async accessSearch(
    applicationId: string,
    question: string,
  ): Promise<AccessSearchResponse> {
    return request<AccessSearchResponse>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/ai/access-search`,
      {
        method: "POST",
        body: JSON.stringify({ question }),
      },
    );
  },
  async platformAccessSearch(question: string): Promise<AccessSearchResponse> {
    return request<AccessSearchResponse>(`/v1/admin/ai/access-search`, {
      method: "POST",
      body: JSON.stringify({ question }),
    });
  },
  async reviewAccess(
    subjectEmail: string,
    applicationId?: string,
  ): Promise<AccessReviewResponse> {
    return request<AccessReviewResponse>(`/v1/admin/ai/access-review/summarize`, {
      method: "POST",
      body: JSON.stringify({ subjectEmail, applicationId }),
    });
  },
  async narrateAudit(
    input: AuditNarrativeInput = {},
  ): Promise<AuditNarrativeResponse> {
    return request<AuditNarrativeResponse>(`/v1/admin/ai/audit/narrative`, {
      method: "POST",
      body: JSON.stringify(input),
    });
  },
  async getAiUsage(windowDays?: number): Promise<AiUsageResponse> {
    const query =
      windowDays && windowDays > 0 ? `?windowDays=${windowDays}` : "";
    return request<AiUsageResponse>(`/v1/admin/ai/usage${query}`);
  },
  async getAiPromptLogs(
    input?: AiPromptLogQuery,
  ): Promise<AiPromptLogResponse> {
    const params = new URLSearchParams();
    if (input?.windowDays && input.windowDays > 0) {
      params.set("windowDays", String(input.windowDays));
    }
    if (input?.feature) {
      params.set("feature", input.feature);
    }
    if (input?.outcome) {
      params.set("outcome", input.outcome);
    }
    if (input?.failuresOnly) {
      params.set("failuresOnly", "true");
    }
    if (input?.page && input.page > 0) {
      params.set("page", String(input.page));
    }
    if (input?.pageSize && input.pageSize > 0) {
      params.set("pageSize", String(input.pageSize));
    }
    const query = params.toString();
    return request<AiPromptLogResponse>(
      `/v1/admin/ai/prompt-logs${query ? `?${query}` : ""}`,
    );
  },
  async getSodRules(applicationId: string): Promise<SodRulesResponse> {
    // Deterministic — available whenever the caller can view the app, regardless of AI.
    return request<SodRulesResponse>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/insights/sod-rules`,
    );
  },
  async getSodViolations(
    applicationId: string,
  ): Promise<SodViolationsResponse> {
    // Deterministic scan — available whenever the caller can view the app, regardless of AI.
    return request<SodViolationsResponse>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/insights/sod-violations`,
    );
  },
  async draftSodRule(
    applicationId: string,
    instruction: string,
  ): Promise<SodRuleDraft> {
    return request<SodRuleDraft>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/ai/sod/rules/draft`,
      {
        method: "POST",
        body: JSON.stringify({ instruction }),
      },
    );
  },
  async saveSodRule(
    applicationId: string,
    input: SodRuleSaveInput,
  ): Promise<SodRule> {
    return request<SodRule>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/ai/sod/rules`,
      {
        method: "POST",
        body: JSON.stringify(input),
      },
    );
  },
  async deleteSodRule(
    applicationId: string,
    ruleKey: string,
  ): Promise<void> {
    await request<void>(
      `/v1/admin/applications/${encodeURIComponent(applicationId)}/ai/sod/rules/${encodeURIComponent(ruleKey)}`,
      {
        method: "DELETE",
      },
    );
  },
};

// ── Utilities ─────────────────────────────────────────────────────────────────

export function userFacingError(error: unknown): string {
  if (error instanceof PortalApiError) return error.message;
  return "Something went wrong. Try the action again.";
}
