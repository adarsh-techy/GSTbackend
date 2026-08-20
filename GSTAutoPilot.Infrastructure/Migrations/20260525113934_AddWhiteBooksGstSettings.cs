using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GSTAutoPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWhiteBooksGstSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WhiteBooksGstClientId",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WhiteBooksGstClientSecret",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "WhiteBooksGstEnabled",
                table: "TenantSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WhiteBooksGstClientId",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "WhiteBooksGstClientSecret",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "WhiteBooksGstEnabled",
                table: "TenantSettings");
        }
    }
}
