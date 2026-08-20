using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GSTAutoPilot.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddFilingTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Gstr1Filings",
                columns: table => new
                {
                    FilingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Period = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AckNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    FiledOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Gstr1Filings", x => x.FilingId);
                });

            migrationBuilder.CreateTable(
                name: "Gstr3bFilings",
                columns: table => new
                {
                    FilingId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Period = table.Column<string>(type: "nvarchar(6)", maxLength: 6, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AckNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    FiledOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PayloadJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Gstr3bFilings", x => x.FilingId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Gstr1Filings_Period_Status",
                table: "Gstr1Filings",
                columns: new[] { "Period", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Gstr3bFilings_Period_Status",
                table: "Gstr3bFilings",
                columns: new[] { "Period", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Gstr1Filings");

            migrationBuilder.DropTable(
                name: "Gstr3bFilings");
        }
    }
}
