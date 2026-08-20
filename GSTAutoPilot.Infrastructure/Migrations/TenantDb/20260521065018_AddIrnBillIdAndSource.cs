using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GSTAutoPilot.Infrastructure.Migrations.TenantDb
{
    /// <inheritdoc />
    public partial class AddIrnBillIdAndSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BillId",
                table: "IRNRecords",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "IRNRecords",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BillId",
                table: "IRNRecords");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "IRNRecords");
        }
    }
}
