using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GSTAutoPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWhiteBooksUserPass : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WhiteBooksPassword",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhiteBooksUsername",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WhiteBooksPassword",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "WhiteBooksUsername",
                table: "TenantSettings");
        }
    }
}
