// State + service seam: React Query hooks wrapping the portal API.
// Presentation never talks to fetch directly — it composes these hooks.

import {
  useMutation,
  useQuery,
  useQueryClient,
  useQueries,
} from "@tanstack/react-query";
import {
  portalApi,
  type CreateApplicationInput,
  type UpdateApplicationInput,
  type CreateAssignmentInput,
  type CreateOidcProviderInput,
  type UpdateOidcProviderInput,
  type OidcProviderValidationInput,
  type CreatePermissionInput,
  type UpdatePermissionInput,
  type CreatePolicyInput,
  type UpdatePolicyInput,
  type CreateReferenceDataInput,
  type UpdateReferenceDataInput,
  type CreateRoleInput,
  type UpdateRoleInput,
  type CreateRolePermissionInput,
  type CreateTenantInput,
  type UpdateTenantInput,
  type UpdateAssignmentInput,
  type SimulatorInput,
  type PolicyDraftInput,
  type ExplainDecisionInput,
  type ImpactAnalysisInput,
  type ApplicationFilters,
  type SodRuleSaveInput,
  type AuditNarrativeInput,
  type AiUsageResponse,
  type AiPromptLogResponse,
  type AiPromptLogQuery,
  type Paged,
  type AssignmentStats,
  type PolicyHistory,
  type DecisionAnalytics,
  type AssignmentImportRequestBody,
  type ReviewCampaign,
  type ReviewCampaignDetail,
} from "../apiClient";
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
  ReferenceDataSummary,
  RolePermissionSummary,
  RoleSummary,
  TenantDetail,
  TenantSummary,
  UserAccess,
  UserDirectoryEntry,
} from "../types";
import { useToast } from "../components/Toast";
import { qk } from "./queryKeys";
import { getRuntimeConfig } from "./runtimeConfig";

// Server-owned cache freshness windows. Functional so each query run reads the
// latest runtime config (which the portal-config query publishes once loaded).
const FRESH = () => getRuntimeConfig().cache.defaultStaleMs;
const VOLATILE = () => getRuntimeConfig().cache.volatileStaleMs;

// ── Queries ─────────────────────────────────────────────────────────────────

export function useTenants() {
  return useQuery<TenantSummary[]>({
    queryKey: qk.tenants,
    queryFn: () => portalApi.listTenants(),
    staleTime: FRESH,
  });
}

export function useApplications() {
  return useQuery<ApplicationSummary[]>({
    queryKey: qk.applications,
    queryFn: () => portalApi.listApplications(),
    staleTime: FRESH,
  });
}

export function useFilteredApplications(
  filters: ApplicationFilters,
  enabled = true,
) {
  return useQuery<ApplicationSummary[]>({
    queryKey: qk.applicationsFiltered(filters),
    queryFn: () => portalApi.listApplications(filters),
    enabled,
    staleTime: FRESH,
  });
}

// Server-paginated applications for the table. `filters` carries the search/
// status/risk/tenant query params plus page/pageSize; the server slices and
// returns the total so the table renders real page controls.
export function useFilteredApplicationsPaged(filters: ApplicationFilters) {
  return useQuery<Paged<ApplicationSummary>>({
    queryKey: qk.applicationsFilteredPaged(filters),
    queryFn: () => portalApi.listApplicationsPaged(filters),
    staleTime: FRESH,
    placeholderData: (prev) => prev,
  });
}

export function usePlatformOverview() {
  return useQuery<PlatformOverview>({
    queryKey: qk.overview,
    queryFn: () => portalApi.getPlatformOverview(),
    staleTime: FRESH,
  });
}

export function useApplicationOverview(appId: string) {
  return useQuery<ApplicationOverview>({
    queryKey: qk.appOverview(appId),
    queryFn: () => portalApi.getApplicationOverview(appId),
    enabled: !!appId,
    staleTime: FRESH,
  });
}

export function useTenantDetail(tenantId: string) {
  return useQuery<TenantDetail>({
    queryKey: qk.tenantDetail(tenantId),
    queryFn: () => portalApi.getTenantDetail(tenantId),
    enabled: !!tenantId,
    staleTime: FRESH,
  });
}

export function useUsersDirectory() {
  return useQuery<UserDirectoryEntry[]>({
    queryKey: qk.users,
    queryFn: () => portalApi.listUsers(),
    staleTime: FRESH,
  });
}

export function useUsersDirectoryPaged(opts: {
  page: number;
  pageSize: number;
  q?: string;
}) {
  return useQuery<Paged<UserDirectoryEntry>>({
    queryKey: qk.usersPaged(opts),
    queryFn: () => portalApi.listUsersPaged(opts),
    staleTime: FRESH,
    placeholderData: (prev) => prev,
  });
}

export function useUserDetail(email: string) {
  return useQuery<UserAccess>({
    queryKey: qk.userDetail(email),
    queryFn: () => portalApi.getUser(email),
    enabled: !!email,
    staleTime: FRESH,
  });
}

// Paged audit feed with server-side search/category. `appId` scopes to one app
// (Activity view); omit for the global Audit view.
export function useAuditFeed(opts: {
  appId?: string;
  q?: string;
  category?: string;
  page: number;
  pageSize: number;
}) {
  return useQuery<Paged<AuditEventSummary>>({
    queryKey: qk.auditFeed(opts),
    queryFn: () =>
      portalApi.listAuditEventsPaged({
        applicationId: opts.appId,
        q: opts.q,
        category: opts.category,
        page: opts.page,
        pageSize: opts.pageSize,
      }),
    staleTime: VOLATILE,
    placeholderData: (prev) => prev,
  });
}

// Per-day activity counts for the calendar heatmap (aggregate, window-bounded).
export function useAuditActivitySummary(opts?: {
  appId?: string;
  days?: number;
}) {
  return useQuery<AuditActivitySummary>({
    queryKey: qk.auditSummary(opts),
    queryFn: () =>
      portalApi.getAuditActivitySummary({
        applicationId: opts?.appId,
        days: opts?.days,
      }),
    staleTime: VOLATILE,
  });
}

export function useAiUsage(windowDays: number) {
  return useQuery<AiUsageResponse>({
    queryKey: qk.aiUsage(windowDays),
    queryFn: () => portalApi.getAiUsage(windowDays),
    staleTime: VOLATILE,
  });
}

export function useAiPromptLogs(query: AiPromptLogQuery) {
  return useQuery<AiPromptLogResponse>({
    queryKey: qk.aiPromptLogs(query),
    queryFn: () => portalApi.getAiPromptLogs(query),
    staleTime: VOLATILE,
    placeholderData: (prev) => prev,
  });
}

export function useRoles(appId: string) {
  return useQuery<RoleSummary[]>({
    queryKey: qk.roles(appId),
    queryFn: () => portalApi.listRoles(appId),
    enabled: !!appId,
    staleTime: FRESH,
  });
}

// Server-paginated roles for the table. Distinct query key from `useRoles`
// (which fetches the full set for matrices/graphs) to avoid cache-shape clashes.
export function useRolesPaged(
  appId: string,
  opts: { page: number; pageSize: number; q?: string; status?: string; privileged?: boolean },
) {
  return useQuery<Paged<RoleSummary>>({
    queryKey: qk.rolesPaged(appId, opts),
    queryFn: () => portalApi.listRolesPaged(appId, opts),
    enabled: !!appId,
    staleTime: FRESH,
    placeholderData: (prev) => prev,
  });
}

export function usePermissions(appId: string) {
  return useQuery<PermissionSummary[]>({
    queryKey: qk.permissions(appId),
    queryFn: () => portalApi.listPermissions(appId),
    enabled: !!appId,
    staleTime: FRESH,
  });
}

export function usePermissionsPaged(
  appId: string,
  opts: { page: number; pageSize: number; q?: string; status?: string },
) {
  return useQuery<Paged<PermissionSummary>>({
    queryKey: qk.permissionsPaged(appId, opts),
    queryFn: () => portalApi.listPermissionsPaged(appId, opts),
    enabled: !!appId,
    staleTime: FRESH,
    placeholderData: (prev) => prev,
  });
}

export function useMappings(appId: string) {
  return useQuery<RolePermissionSummary[]>({
    queryKey: qk.mappings(appId),
    queryFn: () => portalApi.listRoleMappings(appId),
    enabled: !!appId,
    staleTime: FRESH,
  });
}

export function usePolicies(appId: string) {
  return useQuery<PolicySummary[]>({
    queryKey: qk.policies(appId),
    queryFn: () => portalApi.listPolicies(appId),
    enabled: !!appId,
    staleTime: FRESH,
  });
}

export function usePoliciesPaged(
  appId: string,
  opts: { page: number; pageSize: number; q?: string; effect?: string; state?: string },
) {
  return useQuery<Paged<PolicySummary>>({
    queryKey: qk.policiesPaged(appId, opts),
    queryFn: () => portalApi.listPoliciesPaged(appId, opts),
    enabled: !!appId,
    staleTime: FRESH,
    placeholderData: (prev) => prev,
  });
}

export function useReferenceData(appId: string) {
  return useQuery<ReferenceDataSummary[]>({
    queryKey: qk.referenceData(appId),
    queryFn: () => portalApi.listReferenceData(appId),
    enabled: !!appId,
    staleTime: FRESH,
  });
}

export function useReferenceDataPaged(
  appId: string,
  opts: { page: number; pageSize: number; q?: string; status?: string },
) {
  return useQuery<Paged<ReferenceDataSummary>>({
    queryKey: qk.referenceDataPaged(appId, opts),
    queryFn: () => portalApi.listReferenceDataPaged(appId, opts),
    enabled: !!appId,
    staleTime: FRESH,
    placeholderData: (prev) => prev,
  });
}

export function useAssignments(appId: string, enabled = true) {
  return useQuery<AssignmentSummary[]>({
    queryKey: qk.assignments(appId),
    queryFn: () => portalApi.listAssignments(appId),
    enabled: !!appId && enabled,
    staleTime: FRESH,
  });
}

export function useAssignmentsPaged(
  appId: string,
  opts: { page: number; pageSize: number; q?: string; state?: string; expiry?: string },
) {
  return useQuery<Paged<AssignmentSummary>>({
    queryKey: qk.assignmentsPaged(appId, opts),
    queryFn: () => portalApi.listAssignmentsPaged(appId, opts),
    enabled: !!appId,
    staleTime: FRESH,
    placeholderData: (prev) => prev,
  });
}

// Aggregate state counts + total for the assignments donut, so the panel does
// not need to pull every row just to draw the chart. Keyed under the app so the
// standard ["assignments", appId] prefix invalidation refreshes it after mutations.
export function useAssignmentsSummary(appId: string) {
  return useQuery<AssignmentStats>({
    queryKey: qk.assignmentsSummary(appId),
    queryFn: () => portalApi.getAssignmentsSummary(appId),
    enabled: !!appId,
    staleTime: FRESH,
  });
}

export function useOidcProviders(appId: string) {
  return useQuery<OidcProviderSummary[]>({
    queryKey: qk.oidc(appId),
    queryFn: () => portalApi.listOidcProviders(appId),
    enabled: !!appId,
    staleTime: FRESH,
  });
}

export function useAuditEvents(appId: string) {
  return useQuery<AuditEventSummary[]>({
    queryKey: qk.audit(appId),
    queryFn: () => portalApi.listAuditEvents(appId),
    enabled: !!appId,
    staleTime: VOLATILE,
  });
}

// Aggregate assignments across every application (used by the Users and Hierarchy
// surfaces). Each per-app query is cached and reused by the app-scoped hooks.
export function useAllAssignments(appIds: string[]) {
  const results = useQueries({
    queries: appIds.map((id) => ({
      queryKey: qk.assignments(id),
      queryFn: () => portalApi.listAssignments(id),
      enabled: !!id,
      staleTime: FRESH,
    })),
  });
  const byApp = appIds.map((appId, index) => ({
    appId,
    assignments: (results[index]?.data ?? []) as AssignmentSummary[],
  }));
  return {
    byApp,
    isLoading: results.some((r) => r.isLoading),
    isError: results.some((r) => r.isError),
  };
}

// ── Mutation helpers ──────────────────────────────────────────────────────────

function useInvalidate() {
  const qc = useQueryClient();
  return (keys: readonly (readonly unknown[])[]) =>
    Promise.all(keys.map((key) => qc.invalidateQueries({ queryKey: key })));
}

// App-scoped invalidation that also refreshes the platform and per-app overview
// dashboards. Their counts (roles, permissions, policies, assignments) are derived
// from the resource lists a mutation changes, so bundling the aggregate keys keeps
// the dashboard cards and donuts from going stale after a resource mutation.
function useAppInvalidate(appId: string) {
  const invalidate = useInvalidate();
  return (keys: readonly (readonly unknown[])[]) =>
    invalidate([...keys, qk.appOverview(appId), qk.overview]);
}

// ── Application mutations ─────────────────────────────────────────────────────

export function useCreateTenant() {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: CreateTenantInput) => portalApi.createTenant(input),
    onSuccess: async () => {
      await invalidate([qk.tenants]);
      toast.success("Tenant created.");
    },
  });
}

export function useUpdateTenant() {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: UpdateTenantInput) => portalApi.updateTenant(input),
    onSuccess: async () => {
      await invalidate([qk.tenants]);
      toast.success("Tenant updated.");
    },
  });
}

export function useDeleteTenant() {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (tenantId: string) => portalApi.deleteTenant(tenantId),
    onSuccess: async () => {
      await invalidate([qk.tenants]);
      toast.success("Tenant deleted.");
    },
  });
}

export function useCreateApplication() {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: CreateApplicationInput) =>
      portalApi.createApplication(input),
    onSuccess: async () => {
      await invalidate([qk.applications]);
      toast.success("Application created.");
    },
  });
}

export function useUpdateApplication() {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: UpdateApplicationInput) =>
      portalApi.updateApplication(input),
    onSuccess: async () => {
      await invalidate([qk.applications]);
      toast.success("Application updated.");
    },
  });
}

export function useApplicationLifecycle() {
  const toast = useToast();
  const invalidate = useInvalidate();
  const disable = useMutation({
    mutationFn: (appId: string) => portalApi.disableApplication(appId),
    onSuccess: async () => {
      await invalidate([qk.applications]);
      toast.success("Application disabled.");
    },
  });
  const archive = useMutation({
    mutationFn: (appId: string) => portalApi.archiveApplication(appId),
    onSuccess: async () => {
      await invalidate([qk.applications]);
      toast.success("Application archived.");
    },
  });
  const activate = useMutation({
    mutationFn: (appId: string) => portalApi.activateApplication(appId),
    onSuccess: async () => {
      await invalidate([qk.applications]);
      toast.success("Application activated.");
    },
  });
  return { disable, archive, activate };
}

// ── Role / permission / policy mutations ──────────────────────────────────────

export function useCreateRole(appId: string) {
  const toast = useToast();
  const invalidate = useAppInvalidate(appId);
  return useMutation({
    mutationFn: (input: CreateRoleInput) => portalApi.createRole(appId, input),
    onSuccess: async () => {
      await invalidate([qk.roles(appId)]);
      toast.success("Role created.");
    },
  });
}

export function useUpdateRole(appId: string) {
  const toast = useToast();
  const invalidate = useAppInvalidate(appId);
  return useMutation({
    mutationFn: (input: UpdateRoleInput) => portalApi.updateRole(appId, input),
    onSuccess: async () => {
      await invalidate([qk.roles(appId)]);
      toast.success("Role updated.");
    },
  });
}

export function useDeleteRole(appId: string) {
  const toast = useToast();
  const invalidate = useAppInvalidate(appId);
  return useMutation({
    mutationFn: (roleKey: string) => portalApi.deleteRole(appId, roleKey),
    onSuccess: async () => {
      await invalidate([qk.roles(appId)]);
      toast.success("Role deleted.");
    },
  });
}

export function useRoleLifecycle(appId: string) {
  const toast = useToast();
  const invalidate = useAppInvalidate(appId);
  const disable = useMutation({
    mutationFn: (roleKey: string) =>
      portalApi.setRoleStatus(appId, roleKey, "disable"),
    onSuccess: async () => {
      await invalidate([qk.roles(appId)]);
      toast.success("Role disabled.");
    },
  });
  const archive = useMutation({
    mutationFn: (roleKey: string) =>
      portalApi.setRoleStatus(appId, roleKey, "archive"),
    onSuccess: async () => {
      await invalidate([qk.roles(appId)]);
      toast.success("Role archived.");
    },
  });
  const activate = useMutation({
    mutationFn: (roleKey: string) =>
      portalApi.setRoleStatus(appId, roleKey, "activate"),
    onSuccess: async () => {
      await invalidate([qk.roles(appId)]);
      toast.success("Role activated.");
    },
  });
  return { disable, archive, activate };
}

export function useCreatePermission(appId: string) {
  const toast = useToast();
  const invalidate = useAppInvalidate(appId);
  return useMutation({
    mutationFn: (input: CreatePermissionInput) =>
      portalApi.createPermission(appId, input),
    onSuccess: async () => {
      await invalidate([qk.permissions(appId)]);
      toast.success("Permission created.");
    },
  });
}

export function useUpdatePermission(appId: string) {
  const toast = useToast();
  const invalidate = useAppInvalidate(appId);
  return useMutation({
    mutationFn: (input: UpdatePermissionInput) =>
      portalApi.updatePermission(appId, input),
    onSuccess: async () => {
      await invalidate([qk.permissions(appId)]);
      toast.success("Permission updated.");
    },
  });
}

export function useDeletePermission(appId: string) {
  const toast = useToast();
  const invalidate = useAppInvalidate(appId);
  return useMutation({
    mutationFn: (permissionKey: string) =>
      portalApi.deletePermission(appId, permissionKey),
    onSuccess: async () => {
      await invalidate([qk.permissions(appId)]);
      toast.success("Permission deleted.");
    },
  });
}

export function usePermissionLifecycle(appId: string) {
  const toast = useToast();
  const invalidate = useAppInvalidate(appId);
  const disable = useMutation({
    mutationFn: (permissionKey: string) =>
      portalApi.setPermissionStatus(appId, permissionKey, "disable"),
    onSuccess: async () => {
      await invalidate([qk.permissions(appId)]);
      toast.success("Permission disabled.");
    },
  });
  const archive = useMutation({
    mutationFn: (permissionKey: string) =>
      portalApi.setPermissionStatus(appId, permissionKey, "archive"),
    onSuccess: async () => {
      await invalidate([qk.permissions(appId)]);
      toast.success("Permission archived.");
    },
  });
  const activate = useMutation({
    mutationFn: (permissionKey: string) =>
      portalApi.setPermissionStatus(appId, permissionKey, "activate"),
    onSuccess: async () => {
      await invalidate([qk.permissions(appId)]);
      toast.success("Permission activated.");
    },
  });
  return { disable, archive, activate };
}

export function useCreatePolicy(appId: string) {
  const toast = useToast();
  const invalidate = useAppInvalidate(appId);
  return useMutation({
    mutationFn: (input: CreatePolicyInput) =>
      portalApi.createPolicy(appId, input),
    onSuccess: async () => {
      await invalidate([qk.policies(appId)]);
      toast.success("Policy created.");
    },
  });
}

export function usePublishPolicy(appId: string) {
  const toast = useToast();
  const invalidate = useAppInvalidate(appId);
  return useMutation({
    mutationFn: (policyKey: string) =>
      portalApi.publishPolicy(appId, policyKey),
    onSuccess: async () => {
      await invalidate([qk.policies(appId), qk.policyHistory(appId)]);
      toast.success("Policy published.");
    },
  });
}

export function useUpdatePolicy(appId: string) {
  const toast = useToast();
  const invalidate = useAppInvalidate(appId);
  return useMutation({
    mutationFn: (input: UpdatePolicyInput) =>
      portalApi.updatePolicy(appId, input),
    onSuccess: async () => {
      await invalidate([qk.policies(appId), qk.policyHistory(appId)]);
      toast.success("Policy updated.");
    },
  });
}

export function useDeletePolicy(appId: string) {
  const toast = useToast();
  const invalidate = useAppInvalidate(appId);
  return useMutation({
    mutationFn: (policyKey: string) => portalApi.deletePolicy(appId, policyKey),
    onSuccess: async () => {
      await invalidate([qk.policies(appId)]);
      toast.success("Policy deleted.");
    },
  });
}

// Read-only change timeline for a policy, reconstructed from the audit log.
export function usePolicyHistory(
  appId: string,
  policyKey: string,
  enabled = true,
) {
  return useQuery<PolicyHistory>({
    queryKey: qk.policyHistory(appId, policyKey),
    queryFn: () => portalApi.getPolicyHistory(appId, policyKey),
    enabled: !!appId && !!policyKey && enabled,
    staleTime: VOLATILE,
  });
}

// Read-only aggregate analytics over recorded runtime decisions for an application.
export function useDecisionAnalytics(appId: string, windowDays: number) {
  return useQuery<DecisionAnalytics>({
    queryKey: qk.decisionAnalytics(appId, windowDays),
    queryFn: () => portalApi.getDecisionAnalytics(appId, windowDays),
    enabled: !!appId,
    staleTime: VOLATILE,
  });
}

// ── Certification campaigns ───────────────────────────────────────────────────

export function useReviewCampaigns(appId: string) {
  return useQuery<ReviewCampaign[]>({
    queryKey: qk.reviewCampaigns(appId),
    queryFn: () => portalApi.listReviewCampaigns(appId),
    enabled: !!appId,
    staleTime: VOLATILE,
  });
}

export function useReviewCampaign(appId: string, campaignId: string | null) {
  return useQuery<ReviewCampaignDetail>({
    queryKey: qk.reviewCampaign(appId, campaignId),
    queryFn: () => portalApi.getReviewCampaign(appId, campaignId!),
    enabled: !!appId && !!campaignId,
    staleTime: VOLATILE,
  });
}

export function useCreateReviewCampaign(appId: string) {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: { name: string; dueAt?: string | null }) =>
      portalApi.createReviewCampaign(appId, input),
    onSuccess: async () => {
      await invalidate([qk.reviewCampaigns(appId)]);
      toast.success("Campaign created.");
    },
  });
}

export function useActivateReviewCampaign(appId: string) {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (campaignId: string) =>
      portalApi.activateReviewCampaign(appId, campaignId),
    onSuccess: async () => {
      await invalidate([qk.reviewCampaigns(appId), qk.reviewCampaign(appId)]);
      toast.success("Campaign activated. Review items generated.");
    },
  });
}

export function useDecideReviewItem(appId: string) {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: {
      campaignId: string;
      itemId: string;
      decision: string;
      note?: string;
    }) =>
      portalApi.decideReviewItem(appId, input.campaignId, input.itemId, {
        decision: input.decision,
        note: input.note,
      }),
    onSuccess: async () => {
      await invalidate([qk.reviewCampaign(appId), qk.reviewCampaigns(appId)]);
    },
  });
}

export function useDecideReviewItemsBulk(appId: string) {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: {
      campaignId: string;
      decision: string;
      itemIds?: string[];
      note?: string;
    }) =>
      portalApi.decideReviewItemsBulk(appId, input.campaignId, {
        decision: input.decision,
        itemIds: input.itemIds,
        note: input.note,
      }),
    onSuccess: async () => {
      await invalidate([
        qk.reviewCampaign(appId),
        qk.reviewCampaigns(appId),
      ]);
    },
  });
}

export function useFinalizeReviewCampaign(appId: string) {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (campaignId: string) =>
      portalApi.finalizeReviewCampaign(appId, campaignId),
    onSuccess: async () => {
      await invalidate([
        qk.reviewCampaigns(appId),
        qk.reviewCampaign(appId),
        qk.assignments(appId),
      ]);
      toast.success("Campaign finalized. Approved revocations applied.");
    },
  });
}

export function useCreateReferenceData(appId: string) {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: CreateReferenceDataInput) =>
      portalApi.createReferenceData(appId, input),
    onSuccess: async () => {
      await invalidate([qk.referenceData(appId)]);
      toast.success("Reference data created.");
    },
  });
}

export function useUpdateReferenceData(appId: string) {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: UpdateReferenceDataInput) =>
      portalApi.updateReferenceData(appId, input),
    onSuccess: async () => {
      await invalidate([qk.referenceData(appId)]);
      toast.success("Reference data updated.");
    },
  });
}

export function useDeleteReferenceData(appId: string) {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (key: string) => portalApi.deleteReferenceData(appId, key),
    onSuccess: async () => {
      await invalidate([qk.referenceData(appId)]);
      toast.success("Reference data archived.");
    },
  });
}

// ── Mapping mutations (the relationship editor core) ───────────────────────────

export function useGrantPermission(appId: string) {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: CreateRolePermissionInput) =>
      portalApi.createRolePermission(appId, input),
    onSuccess: async () => {
      await invalidate([qk.mappings(appId), qk.roles(appId)]);
      toast.success("Permission granted.");
    },
  });
}

export function usePublishMapping(appId: string) {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (id: string) => portalApi.publishRoleMapping(appId, id),
    onSuccess: async () => {
      await invalidate([qk.mappings(appId), qk.roles(appId)]);
      toast.success("Grant published.");
    },
  });
}

export function useUnmapPermission(appId: string) {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (id: string) => portalApi.unmapRolePermission(appId, id),
    onSuccess: async () => {
      await invalidate([qk.mappings(appId), qk.roles(appId)]);
      toast.success("Grant revoked.");
    },
  });
}

// Bulk publish several draft role→permission mappings in one action, with a
// single toast + one cache invalidation instead of N of each.
export function useBulkPublishMappings(appId: string) {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (ids: string[]) =>
      Promise.all(ids.map((id) => portalApi.publishRoleMapping(appId, id))),
    onSuccess: async (_result, ids) => {
      await invalidate([qk.mappings(appId), qk.roles(appId)]);
      toast.success(
        `Published ${ids.length} grant${ids.length === 1 ? "" : "s"}.`,
      );
    },
  });
}

// Bulk grant several permissions to a role in one action.
export function useBulkGrantPermissions(appId: string) {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (inputs: CreateRolePermissionInput[]) =>
      Promise.all(
        inputs.map((input) => portalApi.createRolePermission(appId, input)),
      ),
    onSuccess: async (_result, inputs) => {
      await invalidate([qk.mappings(appId), qk.roles(appId)]);
      toast.success(
        `Granted ${inputs.length} permission${inputs.length === 1 ? "" : "s"}.`,
      );
    },
  });
}

// ── Assignment mutations ──────────────────────────────────────────────────────

export function useCreateAssignment(appId: string) {
  const toast = useToast();
  const invalidate = useAppInvalidate(appId);
  return useMutation({
    mutationFn: (input: CreateAssignmentInput) =>
      portalApi.createAssignment(appId, input),
    onSuccess: async () => {
      await invalidate([qk.assignments(appId)]);
      toast.success("Access granted.");
    },
  });
}

export function useRevokeAssignment(appId: string) {
  const toast = useToast();
  const invalidate = useAppInvalidate(appId);
  return useMutation({
    mutationFn: (assignmentId: string) =>
      portalApi.revokeAssignment(appId, assignmentId),
    onSuccess: async () => {
      await invalidate([qk.assignments(appId)]);
      toast.success("Access revoked.");
    },
  });
}

export function useUpdateAssignmentExpiry(appId: string) {
  const toast = useToast();
  const invalidate = useAppInvalidate(appId);
  return useMutation({
    mutationFn: (input: { assignmentId: string; validUntil: string }) =>
      portalApi.updateAssignmentExpiry(
        appId,
        input.assignmentId,
        input.validUntil,
      ),
    onSuccess: async () => {
      await invalidate([qk.assignments(appId)]);
      toast.success("Expiry updated.");
    },
  });
}

export function useUpdateAssignment(appId: string) {
  const toast = useToast();
  const invalidate = useAppInvalidate(appId);
  return useMutation({
    mutationFn: (input: UpdateAssignmentInput) =>
      portalApi.updateAssignment(appId, input),
    onSuccess: async () => {
      await invalidate([qk.assignments(appId)]);
      toast.success("Assignment updated.");
    },
  });
}

// Bulk import assignments from parsed CSV rows. Dry-run validates + previews
// without persisting; a real run applies (Source=IMPORT) and refreshes the list.
export function useImportAssignments(appId: string) {
  const invalidate = useAppInvalidate(appId);
  return useMutation({
    mutationFn: (input: AssignmentImportRequestBody) =>
      portalApi.importAssignments(appId, input),
    onSuccess: async (result) => {
      if (!result.dryRun && result.applied > 0) {
        await invalidate([qk.assignments(appId)]);
      }
    },
  });
}

// Break-glass: grant a short, mandatory-reason emergency (EMERGENCY) assignment
// that auto-expires. Loudly audited server-side.
export function useBreakGlass(appId: string) {
  const toast = useToast();
  const invalidate = useAppInvalidate(appId);
  return useMutation({
    mutationFn: (input: {
      subjectEmail: string;
      roleKey: string;
      reason: string;
      durationHours: number;
    }) => portalApi.breakGlass(appId, input),
    onSuccess: async () => {
      await invalidate([qk.assignments(appId)]);
      toast.success("Break-glass access granted — time-boxed and audited.");
    },
  });
}

// ── OIDC mutations ────────────────────────────────────────────────────────────

export function useCreateOidcProvider(appId: string) {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: CreateOidcProviderInput) =>
      portalApi.createOidcProvider(appId, input),
    onSuccess: async () => {
      await invalidate([qk.oidc(appId)]);
      toast.success("OIDC provider registered.");
    },
  });
}

export function useUpdateOidcProvider(appId: string) {
  const toast = useToast();
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: UpdateOidcProviderInput) =>
      portalApi.updateOidcProvider(appId, input),
    onSuccess: async () => {
      await invalidate([qk.oidc(appId)]);
      toast.success("Identity provider updated.");
    },
  });
}

// Read-only configuration validation for an OIDC provider. Does not persist or
// invalidate anything — it returns per-field PASS/WARN/FAIL checks so the form
// can validate before saving.
export function useValidateOidcProvider(appId: string) {
  return useMutation({
    mutationFn: (input: OidcProviderValidationInput) =>
      portalApi.validateOidcProvider(appId, input),
  });
}

// ── Simulator ─────────────────────────────────────────────────────────────────

export function useSimulate() {
  return useMutation({
    mutationFn: (input: SimulatorInput) => portalApi.simulate(input),
  });
}

// ── AI assistance ─────────────────────────────────────────────────────────────

export function usePolicyDraft() {
  return useMutation({
    mutationFn: (input: PolicyDraftInput) => portalApi.draftPolicy(input),
  });
}

export function useExplainDecision() {
  return useMutation({
    mutationFn: (input: ExplainDecisionInput) => portalApi.explainDecision(input),
  });
}

export function useImpactAnalysis() {
  return useMutation({
    mutationFn: (input: ImpactAnalysisInput) => portalApi.analyzeImpact(input),
  });
}

export function useConfigFindings(appId: string, enabled: boolean) {
  return useQuery({
    queryKey: qk.configFindings(appId),
    queryFn: () => portalApi.getConfigFindings(appId),
    enabled: enabled && appId.length > 0,
    staleTime: 60_000,
  });
}

export function useSummarizeConfigFindings() {
  return useMutation({
    mutationFn: (appId: string) => portalApi.summarizeConfigFindings(appId),
  });
}

export function useAccessSearch() {
  return useMutation({
    mutationFn: (input: { appId: string; question: string }) =>
      portalApi.accessSearch(input.appId, input.question),
  });
}

export function usePlatformAccessSearch() {
  return useMutation({
    mutationFn: (question: string) => portalApi.platformAccessSearch(question),
  });
}

export function useReviewAccess() {
  return useMutation({
    mutationFn: (input: { subjectEmail: string; applicationId?: string }) =>
      portalApi.reviewAccess(input.subjectEmail, input.applicationId),
  });
}

export function useAuditNarrative() {
  return useMutation({
    mutationFn: (input: AuditNarrativeInput) => portalApi.narrateAudit(input),
  });
}

export function useSodRules(appId: string, enabled: boolean) {
  return useQuery({
    queryKey: qk.sodRules(appId),
    queryFn: () => portalApi.getSodRules(appId),
    enabled: enabled && appId.length > 0,
    staleTime: 60_000,
  });
}

export function useSodViolations(appId: string, enabled: boolean) {
  return useQuery({
    queryKey: qk.sodViolations(appId),
    queryFn: () => portalApi.getSodViolations(appId),
    enabled: enabled && appId.length > 0,
    staleTime: 60_000,
  });
}

export function useDraftSodRule() {
  return useMutation({
    mutationFn: (input: { appId: string; instruction: string }) =>
      portalApi.draftSodRule(input.appId, input.instruction),
  });
}

export function useSaveSodRule() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: { appId: string; rule: SodRuleSaveInput }) =>
      portalApi.saveSodRule(input.appId, input.rule),
    onSuccess: async (_data, variables) => {
      await invalidate([
        qk.sodRules(variables.appId),
        qk.sodViolations(variables.appId),
      ]);
    },
  });
}

export function useDeleteSodRule() {
  const invalidate = useInvalidate();
  return useMutation({
    mutationFn: (input: { appId: string; ruleKey: string }) =>
      portalApi.deleteSodRule(input.appId, input.ruleKey),
    onSuccess: async (_data, variables) => {
      await invalidate([
        qk.sodRules(variables.appId),
        qk.sodViolations(variables.appId),
      ]);
    },
  });
}
