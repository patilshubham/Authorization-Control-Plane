using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Authorization.Infrastructure.Persistence;

public sealed class AuthorizationDbContext : DbContext
{
    public const string Schema = "authz";

    public AuthorizationDbContext(DbContextOptions<AuthorizationDbContext> options)
        : base(options)
    {
    }

    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<ApplicationEntity> Applications => Set<ApplicationEntity>();
    public DbSet<OidcProviderEntity> OidcProviders => Set<OidcProviderEntity>();
    public DbSet<RoleEntity> Roles => Set<RoleEntity>();
    public DbSet<PermissionEntity> Permissions => Set<PermissionEntity>();
    public DbSet<RolePermissionEntity> RolePermissions => Set<RolePermissionEntity>();
    public DbSet<AssignmentEntity> Assignments => Set<AssignmentEntity>();
    public DbSet<AssignmentAttributeEntity> AssignmentAttributes => Set<AssignmentAttributeEntity>();
    public DbSet<PolicyEntity> Policies => Set<PolicyEntity>();
    public DbSet<ReferenceDataEntity> ReferenceData => Set<ReferenceDataEntity>();
    public DbSet<SodRuleEntity> SodRules => Set<SodRuleEntity>();
    public DbSet<ReviewCampaignEntity> ReviewCampaigns => Set<ReviewCampaignEntity>();
    public DbSet<ReviewItemEntity> ReviewItems => Set<ReviewItemEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<DecisionEntity> Decisions => Set<DecisionEntity>();
    public DbSet<AiInvocationEntity> AiInvocations => Set<AiInvocationEntity>();
    public DbSet<AiPromptLogEntity> AiPromptLogs => Set<AiPromptLogEntity>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        ConfigureTenants(modelBuilder.Entity<TenantEntity>());
        ConfigureApplications(modelBuilder.Entity<ApplicationEntity>());
        ConfigureOidcProviders(modelBuilder.Entity<OidcProviderEntity>());
        ConfigureRoles(modelBuilder.Entity<RoleEntity>());
        ConfigurePermissions(modelBuilder.Entity<PermissionEntity>());
        ConfigureRolePermissions(modelBuilder.Entity<RolePermissionEntity>());
        ConfigureAssignments(modelBuilder.Entity<AssignmentEntity>());
        ConfigureAssignmentAttributes(modelBuilder.Entity<AssignmentAttributeEntity>());
        ConfigurePolicies(modelBuilder.Entity<PolicyEntity>());
        ConfigureReferenceData(modelBuilder.Entity<ReferenceDataEntity>());
        ConfigureSodRules(modelBuilder.Entity<SodRuleEntity>());
        ConfigureReviewCampaigns(modelBuilder.Entity<ReviewCampaignEntity>());
        ConfigureReviewItems(modelBuilder.Entity<ReviewItemEntity>());
        ConfigureAuditEvents(modelBuilder.Entity<AuditEventEntity>());
        ConfigureDecisions(modelBuilder.Entity<DecisionEntity>());
        ConfigureAiInvocations(modelBuilder.Entity<AiInvocationEntity>());
        ConfigureAiPromptLogs(modelBuilder.Entity<AiPromptLogEntity>());
        base.OnModelCreating(modelBuilder);
    }

    private static void ConfigureTenants(EntityTypeBuilder<TenantEntity> builder)
    {
        builder.ToTable("tenants", table =>
        {
            table.HasCheckConstraint("ck_tenants_status", "status in ('ACTIVE', 'DISABLED', 'ARCHIVED')");
        });
        ConfigureAuditedEntity(builder);
        builder.Property(entity => entity.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(entity => entity.Name).HasColumnName("name").IsRequired();
        builder.Property(entity => entity.Description).HasColumnName("description");
        builder.Property(entity => entity.Status).HasColumnName("status").HasDefaultValue("ACTIVE").IsRequired();
        builder.HasAlternateKey(entity => entity.TenantId);
    }

    private static void ConfigureApplications(EntityTypeBuilder<ApplicationEntity> builder)
    {
        builder.ToTable("applications", table =>
        {
            table.HasCheckConstraint("ck_applications_risk_level", "risk_level in ('LOW', 'MEDIUM', 'HIGH', 'CRITICAL')");
            table.HasCheckConstraint("ck_applications_status", "status in ('ACTIVE', 'DISABLED', 'DEPRECATED', 'ARCHIVED')");
            table.HasCheckConstraint("ck_applications_source_of_truth_mode", "source_of_truth_mode in ('PLATFORM_OWNED', 'EXTERNAL_READ', 'DUAL_WRITE', 'EXTERNAL_OWNED')");
            table.HasCheckConstraint("ck_applications_policy_combining_algorithm", "policy_combining_algorithm in ('deny-overrides', 'allow-overrides', 'first-applicable')");
        });
        ConfigureAuditedEntity(builder);
        builder.Property(entity => entity.ApplicationId).HasColumnName("application_id").IsRequired();
        builder.Property(entity => entity.Name).HasColumnName("name").IsRequired();
        builder.Property(entity => entity.Description).HasColumnName("description");
        builder.Property(entity => entity.TenantRefId).HasColumnName("tenant_ref_id").IsRequired();
        builder.Property(entity => entity.OwnerTeam).HasColumnName("owner_team");
        builder.Property(entity => entity.BusinessOwner).HasColumnName("business_owner");
        builder.Property(entity => entity.TechnicalOwner).HasColumnName("technical_owner");
        builder.Property(entity => entity.RiskLevel).HasColumnName("risk_level").HasDefaultValue("MEDIUM").IsRequired();
        builder.Property(entity => entity.Status).HasColumnName("status").HasDefaultValue("ACTIVE").IsRequired();
        builder.Property(entity => entity.SourceOfTruthMode).HasColumnName("source_of_truth_mode").HasDefaultValue("PLATFORM_OWNED").IsRequired();
        builder.Property(entity => entity.PolicyCombiningAlgorithm).HasColumnName("policy_combining_algorithm").HasDefaultValue("deny-overrides").IsRequired();
        builder.HasAlternateKey(entity => entity.ApplicationId);
        builder.HasIndex(entity => entity.TenantRefId);
        builder.HasOne<TenantEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.TenantRefId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureOidcProviders(EntityTypeBuilder<OidcProviderEntity> builder)
    {
        builder.ToTable("oidc_providers", table =>
        {
            table.HasCheckConstraint("ck_oidc_providers_provider_type", "provider_type in ('OIDC')");
            table.HasCheckConstraint("ck_oidc_providers_no_alg_none", "not ('none' = any(allowed_algorithms))");
        });
        ConfigureAuditedEntity(builder);
        builder.Property(entity => entity.ApplicationRefId).HasColumnName("application_ref_id").IsRequired();
        builder.Property(entity => entity.ProviderType).HasColumnName("provider_type").HasDefaultValue("OIDC").IsRequired();
        builder.Property(entity => entity.Issuer).HasColumnName("issuer").IsRequired();
        builder.Property(entity => entity.Audience).HasColumnName("audience").IsRequired();
        builder.Property(entity => entity.JwksUri).HasColumnName("jwks_uri").IsRequired();
        builder.Property(entity => entity.AllowedAlgorithms).HasColumnName("allowed_algorithms").HasColumnType("text[]").HasDefaultValueSql("ARRAY['RS256']::text[]");
        builder.Property(entity => entity.RequiredScopes).HasColumnName("required_scopes").HasColumnType("text[]").HasDefaultValueSql("ARRAY[]::text[]");
        builder.Property(entity => entity.RequiredClaims).HasColumnName("required_claims").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
        builder.Property(entity => entity.ClaimMappings).HasColumnName("claim_mappings").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
        builder.Property(entity => entity.SubjectType).HasColumnName("subject_type").HasDefaultValue("USER").IsRequired();
        builder.Property(entity => entity.SubjectClaim).HasColumnName("subject_claim").HasDefaultValue("sub").IsRequired();
        builder.Property(entity => entity.Enabled).HasColumnName("enabled").HasDefaultValue(true);
        builder.Property(entity => entity.VersionId).HasColumnName("version_id").HasDefaultValueSql("gen_random_uuid()");
        builder.HasIndex(entity => entity.ApplicationRefId);
        builder.HasOne<ApplicationEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ApplicationRefId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureRoles(EntityTypeBuilder<RoleEntity> builder)
    {
        builder.ToTable("roles", table =>
        {
            table.HasCheckConstraint("ck_roles_risk_level", "risk_level in ('LOW', 'MEDIUM', 'HIGH', 'CRITICAL')");
            table.HasCheckConstraint("ck_roles_status", "status in ('ACTIVE', 'DISABLED', 'DEPRECATED', 'ARCHIVED')");
        });
        ConfigureAuditedEntity(builder);
        builder.Property(entity => entity.ApplicationRefId).HasColumnName("application_ref_id").IsRequired();
        builder.Property(entity => entity.RoleKey).HasColumnName("role_key").IsRequired();
        builder.Property(entity => entity.Name).HasColumnName("name").IsRequired();
        builder.Property(entity => entity.Description).HasColumnName("description");
        builder.Property(entity => entity.Privileged).HasColumnName("privileged").HasDefaultValue(false);
        builder.Property(entity => entity.RiskLevel).HasColumnName("risk_level").HasDefaultValue("MEDIUM").IsRequired();
        builder.Property(entity => entity.Status).HasColumnName("status").HasDefaultValue("ACTIVE").IsRequired();
        builder.HasAlternateKey(entity => new { entity.ApplicationRefId, entity.RoleKey });
        builder.HasIndex(entity => entity.ApplicationRefId);
        builder.HasOne<ApplicationEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ApplicationRefId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigurePermissions(EntityTypeBuilder<PermissionEntity> builder)
    {
        builder.ToTable("permissions", table =>
        {
            table.HasCheckConstraint("ck_permissions_risk_level", "risk_level in ('LOW', 'MEDIUM', 'HIGH', 'CRITICAL')");
            table.HasCheckConstraint("ck_permissions_status", "status in ('ACTIVE', 'DISABLED', 'DEPRECATED', 'ARCHIVED')");
        });
        ConfigureAuditedEntity(builder);
        builder.Property(entity => entity.ApplicationRefId).HasColumnName("application_ref_id").IsRequired();
        builder.Property(entity => entity.PermissionKey).HasColumnName("permission_key").IsRequired();
        builder.Property(entity => entity.Resource).HasColumnName("resource").IsRequired();
        builder.Property(entity => entity.Action).HasColumnName("action").IsRequired();
        builder.Property(entity => entity.Description).HasColumnName("description");
        builder.Property(entity => entity.RiskLevel).HasColumnName("risk_level").HasDefaultValue("MEDIUM").IsRequired();
        builder.Property(entity => entity.Status).HasColumnName("status").HasDefaultValue("ACTIVE").IsRequired();
        builder.HasAlternateKey(entity => new { entity.ApplicationRefId, entity.PermissionKey });
        builder.HasIndex(entity => entity.ApplicationRefId);
        builder.HasOne<ApplicationEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ApplicationRefId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureRolePermissions(EntityTypeBuilder<RolePermissionEntity> builder)
    {
        builder.ToTable("role_permissions", table =>
            table.HasCheckConstraint("ck_role_permissions_state", "state in ('DRAFT', 'REVIEW', 'APPROVED', 'PUBLISHED')"));
        ConfigureAuditedEntity(builder);
        builder.Property(entity => entity.ApplicationRefId).HasColumnName("application_ref_id").IsRequired();
        builder.Property(entity => entity.RoleRefId).HasColumnName("role_ref_id").IsRequired();
        builder.Property(entity => entity.PermissionRefId).HasColumnName("permission_ref_id").IsRequired();
        builder.Property(entity => entity.VersionId).HasColumnName("version_id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(entity => entity.State).HasColumnName("state").HasDefaultValue("DRAFT").IsRequired();
        builder.Property(entity => entity.ValidFrom).HasColumnName("valid_from");
        builder.Property(entity => entity.PublishedAt).HasColumnName("published_at");
        builder.HasIndex(entity => new { entity.ApplicationRefId, entity.RoleRefId, entity.State, entity.PublishedAt });
        builder.HasIndex(entity => new { entity.ApplicationRefId, entity.PermissionRefId, entity.State });
        builder.HasOne<ApplicationEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ApplicationRefId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RoleEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.RoleRefId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PermissionEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.PermissionRefId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureAssignments(EntityTypeBuilder<AssignmentEntity> builder)
    {
        builder.ToTable("assignments", table =>
        {
            table.HasCheckConstraint("ck_assignments_subject_type", "subject_type in ('USER', 'GROUP', 'SERVICE_ACCOUNT', 'EXTERNAL_USER', 'TENANT', 'APPLICATION')");
            table.HasCheckConstraint("ck_assignments_source", "source in ('MANUAL', 'IMPORT', 'WORKFLOW', 'LIFECYCLE', 'EMERGENCY')");
            table.HasCheckConstraint("ck_assignments_state", "state in ('ACTIVE', 'EXPIRED', 'REVOKED')");
        });
        ConfigureAuditedEntity(builder);
        builder.Property(entity => entity.ApplicationRefId).HasColumnName("application_ref_id").IsRequired();
        builder.Property(entity => entity.SubjectType).HasColumnName("subject_type").IsRequired();
        builder.Property(entity => entity.SubjectEmail).HasColumnName("subject_email");
        builder.Property(entity => entity.GroupId).HasColumnName("group_id");
        builder.Property(entity => entity.RoleRefId).HasColumnName("role_ref_id").IsRequired();
        builder.Property(entity => entity.ResourceType).HasColumnName("resource_type");
        builder.Property(entity => entity.ResourceId).HasColumnName("resource_id");
        builder.Property(entity => entity.ValidFrom).HasColumnName("valid_from").HasDefaultValueSql("now()");
        builder.Property(entity => entity.ValidUntil).HasColumnName("valid_until");
        builder.Property(entity => entity.RevokedAt).HasColumnName("revoked_at");
        builder.Property(entity => entity.Source).HasColumnName("source").HasDefaultValue("MANUAL").IsRequired();
        builder.Property(entity => entity.State).HasColumnName("state").HasDefaultValue("ACTIVE").IsRequired();
        builder.Property(entity => entity.Reason).HasColumnName("reason");
        builder.HasIndex(entity => new { entity.ApplicationRefId, entity.SubjectEmail, entity.State, entity.ValidFrom, entity.ValidUntil });
        builder.HasIndex(entity => new { entity.ApplicationRefId, entity.RoleRefId, entity.State });
        builder.HasOne<ApplicationEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ApplicationRefId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RoleEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.RoleRefId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureAssignmentAttributes(EntityTypeBuilder<AssignmentAttributeEntity> builder)
    {
        builder.ToTable("assignment_attributes", table =>
            table.HasCheckConstraint("ck_assignment_attributes_value_type", "value_type in ('string', 'number', 'boolean', 'string[]', 'number[]')"));
        ConfigureAuditedEntity(builder);
        builder.Property(entity => entity.AssignmentId).HasColumnName("assignment_id");
        builder.Property(entity => entity.Name).HasColumnName("name").IsRequired();
        builder.Property(entity => entity.Value).HasColumnName("value").HasColumnType("jsonb").IsRequired();
        builder.Property(entity => entity.ValueType).HasColumnName("value_type").IsRequired();
        builder.HasIndex(entity => new { entity.AssignmentId, entity.Name }).IsUnique();
        builder.HasOne<AssignmentEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.AssignmentId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigurePolicies(EntityTypeBuilder<PolicyEntity> builder)
    {
        builder.ToTable("policies", table =>
        {
            table.HasCheckConstraint("ck_policies_effect", "effect in ('ALLOW', 'DENY')");
            table.HasCheckConstraint("ck_policies_state", "state in ('DRAFT', 'REVIEW', 'APPROVED', 'PUBLISHED')");
        });
        ConfigureAuditedEntity(builder);
        builder.Property(entity => entity.ApplicationRefId).HasColumnName("application_ref_id").IsRequired();
        builder.Property(entity => entity.PolicyKey).HasColumnName("policy_key").IsRequired();
        builder.Property(entity => entity.PermissionRefId).HasColumnName("permission_ref_id").IsRequired();
        builder.Property(entity => entity.Effect).HasColumnName("effect").IsRequired();
        builder.Property(entity => entity.Conditions).HasColumnName("conditions").HasColumnType("jsonb").IsRequired();
        builder.Property(entity => entity.Priority).HasColumnName("priority").HasDefaultValue(0).IsRequired();
        builder.Property(entity => entity.Obligations).HasColumnName("obligations").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb").IsRequired();
        builder.Property(entity => entity.VersionId).HasColumnName("version_id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(entity => entity.State).HasColumnName("state").HasDefaultValue("DRAFT").IsRequired();
        builder.Property(entity => entity.PublishedAt).HasColumnName("published_at");
        builder.HasIndex(entity => new { entity.ApplicationRefId, entity.PolicyKey, entity.VersionId }).IsUnique();
        builder.HasIndex(entity => new { entity.ApplicationRefId, entity.PermissionRefId, entity.State, entity.PublishedAt });
        builder.HasOne<ApplicationEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ApplicationRefId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PermissionEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.PermissionRefId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureReferenceData(EntityTypeBuilder<ReferenceDataEntity> builder)
    {
        builder.ToTable("reference_data", table =>
            table.HasCheckConstraint("ck_reference_data_status", "status in ('ACTIVE', 'ARCHIVED')"));
        ConfigureAuditedEntity(builder);
        builder.Property(entity => entity.ApplicationRefId).HasColumnName("application_ref_id").IsRequired();
        builder.Property(entity => entity.Key).HasColumnName("key").IsRequired();
        builder.Property(entity => entity.Description).HasColumnName("description");
        builder.Property(entity => entity.Value).HasColumnName("value").HasColumnType("jsonb").IsRequired();
        builder.Property(entity => entity.Status).HasColumnName("status").HasDefaultValue("ACTIVE").IsRequired();
        builder.HasAlternateKey(entity => new { entity.ApplicationRefId, entity.Key });
        builder.HasIndex(entity => new { entity.ApplicationRefId, entity.Status });
        builder.HasOne<ApplicationEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ApplicationRefId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureSodRules(EntityTypeBuilder<SodRuleEntity> builder)
    {
        builder.ToTable("sod_rules", table =>
        {
            table.HasCheckConstraint("ck_sod_rules_severity", "severity in ('LOW', 'MEDIUM', 'HIGH', 'CRITICAL')");
            table.HasCheckConstraint("ck_sod_rules_status", "status in ('ACTIVE', 'DISABLED', 'ARCHIVED')");
        });
        ConfigureAuditedEntity(builder);
        builder.Property(entity => entity.ApplicationRefId).HasColumnName("application_ref_id").IsRequired();
        builder.Property(entity => entity.RuleKey).HasColumnName("rule_key").IsRequired();
        builder.Property(entity => entity.Name).HasColumnName("name").IsRequired();
        builder.Property(entity => entity.Rationale).HasColumnName("rationale");
        builder.Property(entity => entity.Severity).HasColumnName("severity").HasDefaultValue("HIGH").IsRequired();
        builder.Property(entity => entity.MatcherA).HasColumnName("matcher_a").HasColumnType("jsonb").IsRequired();
        builder.Property(entity => entity.MatcherB).HasColumnName("matcher_b").HasColumnType("jsonb").IsRequired();
        builder.Property(entity => entity.Status).HasColumnName("status").HasDefaultValue("ACTIVE").IsRequired();
        builder.HasAlternateKey(entity => new { entity.ApplicationRefId, entity.RuleKey });
        builder.HasIndex(entity => entity.ApplicationRefId);
        builder.HasOne<ApplicationEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ApplicationRefId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureReviewCampaigns(EntityTypeBuilder<ReviewCampaignEntity> builder)
    {
        builder.ToTable("review_campaigns", table =>
            table.HasCheckConstraint("ck_review_campaigns_status", "status in ('DRAFT', 'ACTIVE', 'CLOSED')"));
        ConfigureAuditedEntity(builder);
        builder.Property(entity => entity.ApplicationRefId).HasColumnName("application_ref_id").IsRequired();
        builder.Property(entity => entity.Name).HasColumnName("name").IsRequired();
        builder.Property(entity => entity.Status).HasColumnName("status").HasDefaultValue("DRAFT").IsRequired();
        builder.Property(entity => entity.DueAt).HasColumnName("due_at");
        builder.HasIndex(entity => entity.ApplicationRefId);
        builder.HasOne<ApplicationEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.ApplicationRefId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void ConfigureReviewItems(EntityTypeBuilder<ReviewItemEntity> builder)
    {
        builder.ToTable("review_items", table =>
            table.HasCheckConstraint("ck_review_items_decision", "decision in ('PENDING', 'KEEP', 'REVOKE', 'NEEDS_INFO')"));
        ConfigureAuditedEntity(builder);
        builder.Property(entity => entity.CampaignRefId).HasColumnName("campaign_ref_id").IsRequired();
        builder.Property(entity => entity.AssignmentRefId).HasColumnName("assignment_ref_id").IsRequired();
        builder.Property(entity => entity.SubjectEmail).HasColumnName("subject_email").IsRequired();
        builder.Property(entity => entity.RoleKey).HasColumnName("role_key").IsRequired();
        builder.Property(entity => entity.Decision).HasColumnName("decision").HasDefaultValue("PENDING").IsRequired();
        builder.Property(entity => entity.DecisionNote).HasColumnName("decision_note");
        builder.Property(entity => entity.DecidedAt).HasColumnName("decided_at");
        builder.Property(entity => entity.DecidedBy).HasColumnName("decided_by");
        builder.HasIndex(entity => entity.CampaignRefId);
        builder.HasOne<ReviewCampaignEntity>()
            .WithMany()
            .HasForeignKey(entity => entity.CampaignRefId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureAuditEvents(EntityTypeBuilder<AuditEventEntity> builder)
    {
        builder.ToTable("audit_events");
        builder.HasKey(entity => entity.EventId);
        builder.Property(entity => entity.EventId).HasColumnName("event_id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(entity => entity.EventType).HasColumnName("event_type").IsRequired();
        builder.Property(entity => entity.ApplicationId).HasColumnName("application_id");
        builder.Property(entity => entity.ActorEmail).HasColumnName("actor_email");
        builder.Property(entity => entity.ActorRole).HasColumnName("actor_role");
        builder.Property(entity => entity.ActorClientId).HasColumnName("actor_client_id");
        builder.Property(entity => entity.TargetSubjectEmail).HasColumnName("target_subject_email");
        builder.Property(entity => entity.SourceIp).HasColumnName("source_ip").HasColumnType("inet");
        builder.Property(entity => entity.Timestamp).HasColumnName("timestamp").HasDefaultValueSql("now()");
        builder.Property(entity => entity.OldValue).HasColumnName("old_value").HasColumnType("jsonb");
        builder.Property(entity => entity.NewValue).HasColumnName("new_value").HasColumnType("jsonb");
        builder.Property(entity => entity.Reason).HasColumnName("reason");
        builder.Property(entity => entity.CorrelationId).HasColumnName("correlation_id");
        builder.HasIndex(entity => new { entity.ApplicationId, entity.Timestamp }).IsDescending(false, true);
        builder.HasIndex(entity => new { entity.ActorEmail, entity.Timestamp }).IsDescending(false, true);
    }

    private static void ConfigureAiInvocations(EntityTypeBuilder<AiInvocationEntity> builder)
    {
        builder.ToTable("ai_invocations");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(entity => entity.Feature).HasColumnName("feature").IsRequired();
        builder.Property(entity => entity.ActorEmail).HasColumnName("actor_email");
        builder.Property(entity => entity.ActorRole).HasColumnName("actor_role");
        builder.Property(entity => entity.ApplicationId).HasColumnName("application_id");
        builder.Property(entity => entity.Provider).HasColumnName("provider").IsRequired();
        builder.Property(entity => entity.Model).HasColumnName("model");
        builder.Property(entity => entity.Outcome).HasColumnName("outcome").IsRequired();
        builder.Property(entity => entity.LatencyMs).HasColumnName("latency_ms").IsRequired();
        builder.Property(entity => entity.PromptTokens).HasColumnName("prompt_tokens");
        builder.Property(entity => entity.CompletionTokens).HasColumnName("completion_tokens");
        builder.Property(entity => entity.TotalTokens).HasColumnName("total_tokens");
        builder.Property(entity => entity.Timestamp).HasColumnName("timestamp").HasDefaultValueSql("now()");
        builder.Property(entity => entity.CorrelationId).HasColumnName("correlation_id");
        builder.HasIndex(entity => entity.Timestamp).IsDescending(true);
        builder.HasIndex(entity => new { entity.Feature, entity.Timestamp }).IsDescending(false, true);
    }

    private static void ConfigureAiPromptLogs(EntityTypeBuilder<AiPromptLogEntity> builder)
    {
        builder.ToTable("ai_prompt_logs");
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(entity => entity.Feature).HasColumnName("feature").IsRequired();
        builder.Property(entity => entity.ActorEmail).HasColumnName("actor_email");
        builder.Property(entity => entity.ActorRole).HasColumnName("actor_role");
        builder.Property(entity => entity.ApplicationId).HasColumnName("application_id");
        builder.Property(entity => entity.PromptText).HasColumnName("prompt_text").IsRequired();
        builder.Property(entity => entity.Outcome).HasColumnName("outcome").IsRequired();
        builder.Property(entity => entity.ErrorMessage).HasColumnName("error_message");
        builder.Property(entity => entity.Interpretation).HasColumnName("interpretation").HasColumnType("jsonb");
        builder.Property(entity => entity.Provider).HasColumnName("provider").IsRequired();
        builder.Property(entity => entity.Model).HasColumnName("model");
        builder.Property(entity => entity.Timestamp).HasColumnName("timestamp").HasDefaultValueSql("now()");
        builder.Property(entity => entity.CorrelationId).HasColumnName("correlation_id");
        builder.HasIndex(entity => entity.Timestamp).IsDescending(true);
        builder.HasIndex(entity => new { entity.Feature, entity.Timestamp }).IsDescending(false, true);
        builder.HasIndex(entity => new { entity.Outcome, entity.Timestamp }).IsDescending(false, true);
    }

    private static void ConfigureDecisions(EntityTypeBuilder<DecisionEntity> builder)
    {
        builder.ToTable("decisions");
        builder.HasKey(entity => entity.DecisionId);
        builder.Property(entity => entity.DecisionId).HasColumnName("decision_id");
        builder.Property(entity => entity.ApplicationId).HasColumnName("application_id").IsRequired();
        builder.Property(entity => entity.SubjectType).HasColumnName("subject_type").IsRequired();
        builder.Property(entity => entity.SubjectEmail).HasColumnName("subject_email");
        builder.Property(entity => entity.ResourceType).HasColumnName("resource_type").IsRequired();
        builder.Property(entity => entity.ResourceId).HasColumnName("resource_id");
        builder.Property(entity => entity.Action).HasColumnName("action").IsRequired();
        builder.Property(entity => entity.ContextSnapshot).HasColumnName("context_snapshot").HasColumnType("jsonb");
        builder.Property(entity => entity.Allowed).HasColumnName("allowed").IsRequired();
        builder.Property(entity => entity.DenyReason).HasColumnName("deny_reason");
        builder.Property(entity => entity.MatchedRoles).HasColumnName("matched_roles").HasColumnType("text[]").HasDefaultValueSql("ARRAY[]::text[]");
        builder.Property(entity => entity.MatchedPermissions).HasColumnName("matched_permissions").HasColumnType("text[]").HasDefaultValueSql("ARRAY[]::text[]");
        builder.Property(entity => entity.MatchedPolicies).HasColumnName("matched_policies").HasColumnType("text[]").HasDefaultValueSql("ARRAY[]::text[]");
        builder.Property(entity => entity.Obligations).HasColumnName("obligations").HasColumnType("jsonb").HasDefaultValueSql("'[]'::jsonb");
        builder.Property(entity => entity.VersionsUsed).HasColumnName("versions_used").HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
        builder.Property(entity => entity.Timestamp).HasColumnName("timestamp").HasDefaultValueSql("now()");
        builder.Property(entity => entity.CorrelationId).HasColumnName("correlation_id");
        builder.HasIndex(entity => new { entity.ApplicationId, entity.Timestamp }).IsDescending(false, true);
        builder.HasIndex(entity => entity.DecisionId).IsUnique();
        builder.HasIndex(entity => new { entity.SubjectEmail, entity.Timestamp }).IsDescending(false, true);
    }

    private static void ConfigureAuditedEntity<TEntity>(EntityTypeBuilder<TEntity> builder)
        where TEntity : AuditedEntity
    {
        builder.HasKey(entity => entity.Id);
        builder.Property(entity => entity.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(entity => entity.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("now()");
        builder.Property(entity => entity.CreatedBy).HasColumnName("created_by").IsRequired();
        builder.Property(entity => entity.UpdatedAt).HasColumnName("updated_at");
        builder.Property(entity => entity.UpdatedBy).HasColumnName("updated_by");
        builder.Property(entity => entity.Version).HasColumnName("version").HasDefaultValue(1).IsConcurrencyToken();
    }
}
