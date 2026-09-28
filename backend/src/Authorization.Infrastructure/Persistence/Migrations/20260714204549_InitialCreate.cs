using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Authorization.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "authz");

            migrationBuilder.CreateTable(
                name: "ai_invocations",
                schema: "authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    feature = table.Column<string>(type: "text", nullable: false),
                    actor_email = table.Column<string>(type: "text", nullable: true),
                    actor_role = table.Column<string>(type: "text", nullable: true),
                    application_id = table.Column<string>(type: "text", nullable: true),
                    provider = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    latency_ms = table.Column<int>(type: "integer", nullable: false),
                    prompt_tokens = table.Column<int>(type: "integer", nullable: true),
                    completion_tokens = table.Column<int>(type: "integer", nullable: true),
                    total_tokens = table.Column<int>(type: "integer", nullable: true),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_invocations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "ai_prompt_logs",
                schema: "authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    feature = table.Column<string>(type: "text", nullable: false),
                    actor_email = table.Column<string>(type: "text", nullable: true),
                    actor_role = table.Column<string>(type: "text", nullable: true),
                    application_id = table.Column<string>(type: "text", nullable: true),
                    prompt_text = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    interpretation = table.Column<string>(type: "jsonb", nullable: true),
                    provider = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: true),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_prompt_logs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "authz",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    application_id = table.Column<string>(type: "text", nullable: true),
                    actor_email = table.Column<string>(type: "text", nullable: true),
                    actor_role = table.Column<string>(type: "text", nullable: true),
                    actor_client_id = table.Column<string>(type: "text", nullable: true),
                    target_subject_email = table.Column<string>(type: "text", nullable: true),
                    source_ip = table.Column<IPAddress>(type: "inet", nullable: true),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    old_value = table.Column<string>(type: "jsonb", nullable: true),
                    new_value = table.Column<string>(type: "jsonb", nullable: true),
                    reason = table.Column<string>(type: "text", nullable: true),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "decisions",
                schema: "authz",
                columns: table => new
                {
                    decision_id = table.Column<string>(type: "text", nullable: false),
                    application_id = table.Column<string>(type: "text", nullable: false),
                    subject_type = table.Column<string>(type: "text", nullable: false),
                    subject_email = table.Column<string>(type: "text", nullable: true),
                    resource_type = table.Column<string>(type: "text", nullable: false),
                    resource_id = table.Column<string>(type: "text", nullable: true),
                    action = table.Column<string>(type: "text", nullable: false),
                    context_snapshot = table.Column<string>(type: "jsonb", nullable: true),
                    allowed = table.Column<bool>(type: "boolean", nullable: false),
                    deny_reason = table.Column<string>(type: "text", nullable: true),
                    matched_roles = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "ARRAY[]::text[]"),
                    matched_permissions = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "ARRAY[]::text[]"),
                    matched_policies = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "ARRAY[]::text[]"),
                    obligations = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    versions_used = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    correlation_id = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_decisions", x => x.decision_id);
                });

            migrationBuilder.CreateTable(
                name: "tenants",
                schema: "authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    tenant_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "ACTIVE"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenants", x => x.id);
                    table.UniqueConstraint("AK_tenants_tenant_id", x => x.tenant_id);
                    table.CheckConstraint("ck_tenants_status", "status in ('ACTIVE', 'DISABLED', 'ARCHIVED')");
                });

            migrationBuilder.CreateTable(
                name: "applications",
                schema: "authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    application_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    tenant_ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_team = table.Column<string>(type: "text", nullable: true),
                    business_owner = table.Column<string>(type: "text", nullable: true),
                    technical_owner = table.Column<string>(type: "text", nullable: true),
                    risk_level = table.Column<string>(type: "text", nullable: false, defaultValue: "MEDIUM"),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "ACTIVE"),
                    source_of_truth_mode = table.Column<string>(type: "text", nullable: false, defaultValue: "PLATFORM_OWNED"),
                    policy_combining_algorithm = table.Column<string>(type: "text", nullable: false, defaultValue: "deny-overrides"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_applications", x => x.id);
                    table.UniqueConstraint("AK_applications_application_id", x => x.application_id);
                    table.CheckConstraint("ck_applications_policy_combining_algorithm", "policy_combining_algorithm in ('deny-overrides', 'allow-overrides', 'first-applicable')");
                    table.CheckConstraint("ck_applications_risk_level", "risk_level in ('LOW', 'MEDIUM', 'HIGH', 'CRITICAL')");
                    table.CheckConstraint("ck_applications_source_of_truth_mode", "source_of_truth_mode in ('PLATFORM_OWNED', 'EXTERNAL_READ', 'DUAL_WRITE', 'EXTERNAL_OWNED')");
                    table.CheckConstraint("ck_applications_status", "status in ('ACTIVE', 'DISABLED', 'DEPRECATED', 'ARCHIVED')");
                    table.ForeignKey(
                        name: "FK_applications_tenants_tenant_ref_id",
                        column: x => x.tenant_ref_id,
                        principalSchema: "authz",
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "oidc_providers",
                schema: "authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    application_ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider_type = table.Column<string>(type: "text", nullable: false, defaultValue: "OIDC"),
                    issuer = table.Column<string>(type: "text", nullable: false),
                    audience = table.Column<string>(type: "text", nullable: false),
                    jwks_uri = table.Column<string>(type: "text", nullable: false),
                    allowed_algorithms = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "ARRAY['RS256']::text[]"),
                    required_scopes = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "ARRAY[]::text[]"),
                    required_claims = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    claim_mappings = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'{}'::jsonb"),
                    subject_type = table.Column<string>(type: "text", nullable: false, defaultValue: "USER"),
                    subject_claim = table.Column<string>(type: "text", nullable: false, defaultValue: "sub"),
                    enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_oidc_providers", x => x.id);
                    table.CheckConstraint("ck_oidc_providers_no_alg_none", "not ('none' = any(allowed_algorithms))");
                    table.CheckConstraint("ck_oidc_providers_provider_type", "provider_type in ('OIDC')");
                    table.ForeignKey(
                        name: "FK_oidc_providers_applications_application_ref_id",
                        column: x => x.application_ref_id,
                        principalSchema: "authz",
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "permissions",
                schema: "authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    application_ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_key = table.Column<string>(type: "text", nullable: false),
                    resource = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    risk_level = table.Column<string>(type: "text", nullable: false, defaultValue: "MEDIUM"),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "ACTIVE"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_permissions", x => x.id);
                    table.UniqueConstraint("AK_permissions_application_ref_id_permission_key", x => new { x.application_ref_id, x.permission_key });
                    table.CheckConstraint("ck_permissions_risk_level", "risk_level in ('LOW', 'MEDIUM', 'HIGH', 'CRITICAL')");
                    table.CheckConstraint("ck_permissions_status", "status in ('ACTIVE', 'DISABLED', 'DEPRECATED', 'ARCHIVED')");
                    table.ForeignKey(
                        name: "FK_permissions_applications_application_ref_id",
                        column: x => x.application_ref_id,
                        principalSchema: "authz",
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "reference_data",
                schema: "authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    application_ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "ACTIVE"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reference_data", x => x.id);
                    table.UniqueConstraint("AK_reference_data_application_ref_id_key", x => new { x.application_ref_id, x.key });
                    table.CheckConstraint("ck_reference_data_status", "status in ('ACTIVE', 'ARCHIVED')");
                    table.ForeignKey(
                        name: "FK_reference_data_applications_application_ref_id",
                        column: x => x.application_ref_id,
                        principalSchema: "authz",
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    application_ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    privileged = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    risk_level = table.Column<string>(type: "text", nullable: false, defaultValue: "MEDIUM"),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "ACTIVE"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.id);
                    table.UniqueConstraint("AK_roles_application_ref_id_role_key", x => new { x.application_ref_id, x.role_key });
                    table.CheckConstraint("ck_roles_risk_level", "risk_level in ('LOW', 'MEDIUM', 'HIGH', 'CRITICAL')");
                    table.CheckConstraint("ck_roles_status", "status in ('ACTIVE', 'DISABLED', 'DEPRECATED', 'ARCHIVED')");
                    table.ForeignKey(
                        name: "FK_roles_applications_application_ref_id",
                        column: x => x.application_ref_id,
                        principalSchema: "authz",
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sod_rules",
                schema: "authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    application_ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    rule_key = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    rationale = table.Column<string>(type: "text", nullable: true),
                    severity = table.Column<string>(type: "text", nullable: false, defaultValue: "HIGH"),
                    matcher_a = table.Column<string>(type: "jsonb", nullable: false),
                    matcher_b = table.Column<string>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "ACTIVE"),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sod_rules", x => x.id);
                    table.UniqueConstraint("AK_sod_rules_application_ref_id_rule_key", x => new { x.application_ref_id, x.rule_key });
                    table.CheckConstraint("ck_sod_rules_severity", "severity in ('LOW', 'MEDIUM', 'HIGH', 'CRITICAL')");
                    table.CheckConstraint("ck_sod_rules_status", "status in ('ACTIVE', 'DISABLED', 'ARCHIVED')");
                    table.ForeignKey(
                        name: "FK_sod_rules_applications_application_ref_id",
                        column: x => x.application_ref_id,
                        principalSchema: "authz",
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "policies",
                schema: "authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    application_ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    policy_key = table.Column<string>(type: "text", nullable: false),
                    permission_ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    effect = table.Column<string>(type: "text", nullable: false),
                    conditions = table.Column<string>(type: "jsonb", nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    obligations = table.Column<string>(type: "jsonb", nullable: false, defaultValueSql: "'[]'::jsonb"),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    state = table.Column<string>(type: "text", nullable: false, defaultValue: "DRAFT"),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policies", x => x.id);
                    table.CheckConstraint("ck_policies_effect", "effect in ('ALLOW', 'DENY')");
                    table.CheckConstraint("ck_policies_state", "state in ('DRAFT', 'REVIEW', 'APPROVED', 'PUBLISHED', 'ROLLED_BACK')");
                    table.ForeignKey(
                        name: "FK_policies_applications_application_ref_id",
                        column: x => x.application_ref_id,
                        principalSchema: "authz",
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_policies_permissions_permission_ref_id",
                        column: x => x.permission_ref_id,
                        principalSchema: "authz",
                        principalTable: "permissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "assignments",
                schema: "authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    application_ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_type = table.Column<string>(type: "text", nullable: false),
                    subject_email = table.Column<string>(type: "text", nullable: true),
                    group_id = table.Column<string>(type: "text", nullable: true),
                    role_ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    resource_type = table.Column<string>(type: "text", nullable: true),
                    resource_id = table.Column<string>(type: "text", nullable: true),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    source = table.Column<string>(type: "text", nullable: false, defaultValue: "MANUAL"),
                    state = table.Column<string>(type: "text", nullable: false, defaultValue: "ACTIVE"),
                    reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assignments", x => x.id);
                    table.CheckConstraint("ck_assignments_source", "source in ('MANUAL', 'IMPORT', 'WORKFLOW', 'LIFECYCLE', 'EMERGENCY')");
                    table.CheckConstraint("ck_assignments_state", "state in ('ACTIVE', 'EXPIRED', 'REVOKED')");
                    table.CheckConstraint("ck_assignments_subject_type", "subject_type in ('USER', 'GROUP', 'SERVICE_ACCOUNT', 'EXTERNAL_USER', 'TENANT', 'APPLICATION')");
                    table.ForeignKey(
                        name: "FK_assignments_applications_application_ref_id",
                        column: x => x.application_ref_id,
                        principalSchema: "authz",
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_assignments_roles_role_ref_id",
                        column: x => x.role_ref_id,
                        principalSchema: "authz",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                schema: "authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    application_ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role_ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    permission_ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    state = table.Column<string>(type: "text", nullable: false, defaultValue: "DRAFT"),
                    valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permissions", x => x.id);
                    table.CheckConstraint("ck_role_permissions_state", "state in ('DRAFT', 'REVIEW', 'APPROVED', 'PUBLISHED', 'ROLLED_BACK')");
                    table.ForeignKey(
                        name: "FK_role_permissions_applications_application_ref_id",
                        column: x => x.application_ref_id,
                        principalSchema: "authz",
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_role_permissions_permissions_permission_ref_id",
                        column: x => x.permission_ref_id,
                        principalSchema: "authz",
                        principalTable: "permissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_role_permissions_roles_role_ref_id",
                        column: x => x.role_ref_id,
                        principalSchema: "authz",
                        principalTable: "roles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "assignment_attributes",
                schema: "authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    value = table.Column<string>(type: "jsonb", nullable: false),
                    value_type = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assignment_attributes", x => x.id);
                    table.CheckConstraint("ck_assignment_attributes_value_type", "value_type in ('string', 'number', 'boolean', 'string[]', 'number[]')");
                    table.ForeignKey(
                        name: "FK_assignment_attributes_assignments_assignment_id",
                        column: x => x.assignment_id,
                        principalSchema: "authz",
                        principalTable: "assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ai_invocations_feature_timestamp",
                schema: "authz",
                table: "ai_invocations",
                columns: new[] { "feature", "timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ai_invocations_timestamp",
                schema: "authz",
                table: "ai_invocations",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_logs_feature_timestamp",
                schema: "authz",
                table: "ai_prompt_logs",
                columns: new[] { "feature", "timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_logs_outcome_timestamp",
                schema: "authz",
                table: "ai_prompt_logs",
                columns: new[] { "outcome", "timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_ai_prompt_logs_timestamp",
                schema: "authz",
                table: "ai_prompt_logs",
                column: "timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_applications_tenant_ref_id",
                schema: "authz",
                table: "applications",
                column: "tenant_ref_id");

            migrationBuilder.CreateIndex(
                name: "IX_assignment_attributes_assignment_id_name",
                schema: "authz",
                table: "assignment_attributes",
                columns: new[] { "assignment_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assignments_application_ref_id_role_ref_id_state",
                schema: "authz",
                table: "assignments",
                columns: new[] { "application_ref_id", "role_ref_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_assignments_application_ref_id_subject_email_state_valid_fr~",
                schema: "authz",
                table: "assignments",
                columns: new[] { "application_ref_id", "subject_email", "state", "valid_from", "valid_until" });

            migrationBuilder.CreateIndex(
                name: "IX_assignments_role_ref_id",
                schema: "authz",
                table: "assignments",
                column: "role_ref_id");

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_actor_email_timestamp",
                schema: "authz",
                table: "audit_events",
                columns: new[] { "actor_email", "timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_application_id_timestamp",
                schema: "authz",
                table: "audit_events",
                columns: new[] { "application_id", "timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_decisions_application_id_timestamp",
                schema: "authz",
                table: "decisions",
                columns: new[] { "application_id", "timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_decisions_decision_id",
                schema: "authz",
                table: "decisions",
                column: "decision_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_decisions_subject_email_timestamp",
                schema: "authz",
                table: "decisions",
                columns: new[] { "subject_email", "timestamp" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_oidc_providers_application_ref_id",
                schema: "authz",
                table: "oidc_providers",
                column: "application_ref_id");

            migrationBuilder.CreateIndex(
                name: "IX_permissions_application_ref_id",
                schema: "authz",
                table: "permissions",
                column: "application_ref_id");

            migrationBuilder.CreateIndex(
                name: "IX_policies_application_ref_id_permission_ref_id_state_publish~",
                schema: "authz",
                table: "policies",
                columns: new[] { "application_ref_id", "permission_ref_id", "state", "published_at" });

            migrationBuilder.CreateIndex(
                name: "IX_policies_application_ref_id_policy_key_version_id",
                schema: "authz",
                table: "policies",
                columns: new[] { "application_ref_id", "policy_key", "version_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_policies_permission_ref_id",
                schema: "authz",
                table: "policies",
                column: "permission_ref_id");

            migrationBuilder.CreateIndex(
                name: "IX_reference_data_application_ref_id_status",
                schema: "authz",
                table: "reference_data",
                columns: new[] { "application_ref_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_role_permissions_application_ref_id_permission_ref_id_state",
                schema: "authz",
                table: "role_permissions",
                columns: new[] { "application_ref_id", "permission_ref_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_role_permissions_application_ref_id_role_ref_id_state_publi~",
                schema: "authz",
                table: "role_permissions",
                columns: new[] { "application_ref_id", "role_ref_id", "state", "published_at" });

            migrationBuilder.CreateIndex(
                name: "IX_role_permissions_permission_ref_id",
                schema: "authz",
                table: "role_permissions",
                column: "permission_ref_id");

            migrationBuilder.CreateIndex(
                name: "IX_role_permissions_role_ref_id",
                schema: "authz",
                table: "role_permissions",
                column: "role_ref_id");

            migrationBuilder.CreateIndex(
                name: "IX_roles_application_ref_id",
                schema: "authz",
                table: "roles",
                column: "application_ref_id");

            migrationBuilder.CreateIndex(
                name: "IX_sod_rules_application_ref_id",
                schema: "authz",
                table: "sod_rules",
                column: "application_ref_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_invocations",
                schema: "authz");

            migrationBuilder.DropTable(
                name: "ai_prompt_logs",
                schema: "authz");

            migrationBuilder.DropTable(
                name: "assignment_attributes",
                schema: "authz");

            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "authz");

            migrationBuilder.DropTable(
                name: "decisions",
                schema: "authz");

            migrationBuilder.DropTable(
                name: "oidc_providers",
                schema: "authz");

            migrationBuilder.DropTable(
                name: "policies",
                schema: "authz");

            migrationBuilder.DropTable(
                name: "reference_data",
                schema: "authz");

            migrationBuilder.DropTable(
                name: "role_permissions",
                schema: "authz");

            migrationBuilder.DropTable(
                name: "sod_rules",
                schema: "authz");

            migrationBuilder.DropTable(
                name: "assignments",
                schema: "authz");

            migrationBuilder.DropTable(
                name: "permissions",
                schema: "authz");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "authz");

            migrationBuilder.DropTable(
                name: "applications",
                schema: "authz");

            migrationBuilder.DropTable(
                name: "tenants",
                schema: "authz");
        }
    }
}
