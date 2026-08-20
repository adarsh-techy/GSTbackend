using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GSTAutoPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSmtpSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SmtpEnableSsl",
                table: "TenantSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SmtpFromEmail",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpFromName",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpHost",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SmtpPassword",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SmtpPort",
                table: "TenantSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SmtpUsername",
                table: "TenantSettings",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SmtpEnableSsl",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "SmtpFromEmail",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "SmtpFromName",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "SmtpHost",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "SmtpPassword",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "SmtpPort",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "SmtpUsername",
                table: "TenantSettings");
        }
    }
}
