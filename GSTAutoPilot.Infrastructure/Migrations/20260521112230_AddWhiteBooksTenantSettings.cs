using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GSTAutoPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWhiteBooksTenantSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WhiteBooksClientId",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhiteBooksClientSecret",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WhiteBooksEnabled",
                table: "TenantSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "WhiteBooksUseSandbox",
                table: "TenantSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WhiteBooksClientId",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "WhiteBooksClientSecret",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "WhiteBooksEnabled",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "WhiteBooksUseSandbox",
                table: "TenantSettings");
        }
    }
}
