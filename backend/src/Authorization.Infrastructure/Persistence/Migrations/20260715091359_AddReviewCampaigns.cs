using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Authorization.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "review_campaigns",
                schema: "authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    application_ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false, defaultValue: "DRAFT"),
                    due_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_campaigns", x => x.id);
                    table.CheckConstraint("ck_review_campaigns_status", "status in ('DRAFT', 'ACTIVE', 'CLOSED')");
                    table.ForeignKey(
                        name: "FK_review_campaigns_applications_application_ref_id",
                        column: x => x.application_ref_id,
                        principalSchema: "authz",
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "review_items",
                schema: "authz",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    campaign_ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_ref_id = table.Column<Guid>(type: "uuid", nullable: false),
                    subject_email = table.Column<string>(type: "text", nullable: false),
                    role_key = table.Column<string>(type: "text", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false, defaultValue: "PENDING"),
                    decision_note = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    decided_by = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<string>(type: "text", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_review_items", x => x.id);
                    table.CheckConstraint("ck_review_items_decision", "decision in ('PENDING', 'KEEP', 'REVOKE', 'NEEDS_INFO')");
                    table.ForeignKey(
                        name: "FK_review_items_review_campaigns_campaign_ref_id",
                        column: x => x.campaign_ref_id,
                        principalSchema: "authz",
                        principalTable: "review_campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_review_campaigns_application_ref_id",
                schema: "authz",
                table: "review_campaigns",
                column: "application_ref_id");

            migrationBuilder.CreateIndex(
                name: "IX_review_items_campaign_ref_id",
                schema: "authz",
                table: "review_items",
                column: "campaign_ref_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "review_items",
                schema: "authz");

            migrationBuilder.DropTable(
                name: "review_campaigns",
                schema: "authz");
        }
    }
}
