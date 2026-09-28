using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Authorization.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveRollbackState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_role_permissions_state",
                schema: "authz",
                table: "role_permissions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_policies_state",
                schema: "authz",
                table: "policies");

            migrationBuilder.AddCheckConstraint(
                name: "ck_role_permissions_state",
                schema: "authz",
                table: "role_permissions",
                sql: "state in ('DRAFT', 'REVIEW', 'APPROVED', 'PUBLISHED')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_policies_state",
                schema: "authz",
                table: "policies",
                sql: "state in ('DRAFT', 'REVIEW', 'APPROVED', 'PUBLISHED')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_role_permissions_state",
                schema: "authz",
                table: "role_permissions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_policies_state",
                schema: "authz",
                table: "policies");

            migrationBuilder.AddCheckConstraint(
                name: "ck_role_permissions_state",
                schema: "authz",
                table: "role_permissions",
                sql: "state in ('DRAFT', 'REVIEW', 'APPROVED', 'PUBLISHED', 'ROLLED_BACK')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_policies_state",
                schema: "authz",
                table: "policies",
                sql: "state in ('DRAFT', 'REVIEW', 'APPROVED', 'PUBLISHED', 'ROLLED_BACK')");
        }
    }
}
