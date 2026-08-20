using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GSTAutoPilot.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddEWayBills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EWayBills",
                columns: table => new
                {
                    EWBId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EWBNumber = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    GeneratedDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ValidUntil = table.Column<DateTime>(type: "datetime2", nullable: false),
                    FromGSTIN = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    FromAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    ToGSTIN = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    ToAddress = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    TransporterGSTIN = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: false),
                    TransporterName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    VehicleNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Distance = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    Mode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CancelledOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EWayBills", x => x.EWBId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EWayBills_EWBNumber",
                table: "EWayBills",
                column: "EWBNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EWayBills_InvoiceId",
                table: "EWayBills",
                column: "InvoiceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EWayBills");
        }
    }
}
