using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GSTAutoPilot.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddReconSupplierName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SupplierName",
                table: "ReconResults",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SupplierName",
                table: "ReconResults");
        }
    }
}
