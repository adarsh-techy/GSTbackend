using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GSTAutoPilot.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddIRNRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IRNRecords",
                columns: table => new
                {
                    IRNId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvoiceNo = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    IRNNumber = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    QRCode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AcknowledgementNo = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    AcknowledgementDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SignedInvoice = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CancelledOn = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CancelReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IRNRecords", x => x.IRNId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IRNRecords_InvoiceId",
                table: "IRNRecords",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_IRNRecords_IRNNumber",
                table: "IRNRecords",
                column: "IRNNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IRNRecords");
        }
    }
}
