using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GSTAutoPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantErpProfile : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SalesDocId",
                table: "Tenants",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SalesHeaderTable",
                table: "Tenants",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "SalesLineTable",
                table: "Tenants",
                type: "nvarchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SalesDocId",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SalesHeaderTable",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "SalesLineTable",
                table: "Tenants");
        }
    }
}
