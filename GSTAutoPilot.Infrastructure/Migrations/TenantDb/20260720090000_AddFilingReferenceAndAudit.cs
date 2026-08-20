using System;
using GSTAutoPilot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GSTAutoPilot.Infrastructure.Migrations.TenantDb
{
    // Hand-authored (no dotnet-ef on this machine), so the [DbContext]/[Migration]
    // attributes live here rather than in a generated .Designer.cs. The model
    // snapshot in TenantDbContextModelSnapshot.cs was updated to match.
    [DbContext(typeof(TenantDbContext))]
    [Migration("20260720090000_AddFilingReferenceAndAudit")]
    /// <inheritdoc />
    public partial class AddFilingReferenceAndAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // GSTN's reference id correlates retsave -> retsubmit -> retfile.
            // Without it a part-completed filing cannot be resumed or traced.
            migrationBuilder.AddColumn<string>(
                name: "ReferenceId",
                table: "Gstr1Filings",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            // GSTN's validation report from the last retsave, when rows were rejected.
            migrationBuilder.AddColumn<string>(
                name: "ErrorReportJson",
                table: "Gstr1Filings",
                type: "nvarchar(max)",
                nullable: true);

            // When the return was locked on GSTN (retsubmit) — distinct from FiledOn.
            migrationBuilder.AddColumn<DateTime>(
                name: "SubmittedOn",
                table: "Gstr1Filings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FiledBy",
                table: "Gstr1Filings",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferenceId",
                table: "Gstr3bFilings",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ErrorReportJson",
                table: "Gstr3bFilings",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SubmittedOn",
                table: "Gstr3bFilings",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FiledBy",
                table: "Gstr3bFilings",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            // Challan identification number, when 3B tax was paid by challan.
            migrationBuilder.AddColumn<string>(
                name: "Cin",
                table: "Gstr3bFilings",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ReferenceId",
                table: "Gstr1Filings");

            migrationBuilder.DropColumn(
                name: "ErrorReportJson",
                table: "Gstr1Filings");

            migrationBuilder.DropColumn(
                name: "SubmittedOn",
                table: "Gstr1Filings");

            migrationBuilder.DropColumn(
                name: "FiledBy",
                table: "Gstr1Filings");

            migrationBuilder.DropColumn(
                name: "ReferenceId",
                table: "Gstr3bFilings");

            migrationBuilder.DropColumn(
                name: "ErrorReportJson",
                table: "Gstr3bFilings");

            migrationBuilder.DropColumn(
                name: "SubmittedOn",
                table: "Gstr3bFilings");

            migrationBuilder.DropColumn(
                name: "FiledBy",
                table: "Gstr3bFilings");

            migrationBuilder.DropColumn(
                name: "Cin",
                table: "Gstr3bFilings");
        }
    }
}
