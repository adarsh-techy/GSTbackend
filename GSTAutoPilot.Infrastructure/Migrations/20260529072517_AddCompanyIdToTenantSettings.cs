using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GSTAutoPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanyIdToTenantSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantSettings_TenantId",
                table: "TenantSettings");

            migrationBuilder.AddColumn<byte>(
                name: "CompanyId",
                table: "TenantSettings",
                type: "tinyint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_TenantSettings_Tenant_Company",
                table: "TenantSettings",
                columns: new[] { "TenantId", "CompanyId" },
                unique: true,
                filter: "[CompanyId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_TenantSettings_Tenant_Default",
                table: "TenantSettings",
                column: "TenantId",
                unique: true,
                filter: "[CompanyId] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_TenantSettings_Tenant_Company",
                table: "TenantSettings");

            migrationBuilder.DropIndex(
                name: "UX_TenantSettings_Tenant_Default",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "CompanyId",
                table: "TenantSettings");

            migrationBuilder.CreateIndex(
                name: "IX_TenantSettings_TenantId",
                table: "TenantSettings",
                column: "TenantId",
                unique: true);
        }
    }
}
