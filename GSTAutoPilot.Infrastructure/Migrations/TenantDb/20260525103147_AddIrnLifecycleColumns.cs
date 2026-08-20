using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GSTAutoPilot.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddIrnLifecycleColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CancelRemarks",
                table: "IRNRecords",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EmailSentOn",
                table: "IRNRecords",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmailSentTo",
                table: "IRNRecords",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "JsonDownloadedOn",
                table: "IRNRecords",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancelRemarks",
                table: "IRNRecords");

            migrationBuilder.DropColumn(
                name: "EmailSentOn",
                table: "IRNRecords");

            migrationBuilder.DropColumn(
                name: "EmailSentTo",
                table: "IRNRecords");

            migrationBuilder.DropColumn(
                name: "JsonDownloadedOn",
                table: "IRNRecords");
        }
    }
}
