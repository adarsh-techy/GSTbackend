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
    [Migration("20260721093000_AddGstr2bItcEligibility")]
    /// <inheritdoc />
    public partial class AddGstr2bItcEligibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // GSTR-2B per-invoice ITC availability (itcavl "Y"/"N") + reason (rsn),
            // so credit the portal marks ineligible (PoS rule, 16(4) time-bar) is
            // not claimed. Existing rows default to eligible.
            migrationBuilder.AddColumn<bool>(
                name: "IsItcEligible",
                table: "GSTR2BRecords",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "ItcIneligibleReason",
                table: "GSTR2BRecords",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsItcEligible",
                table: "GSTR2BRecords");

            migrationBuilder.DropColumn(
                name: "ItcIneligibleReason",
                table: "GSTR2BRecords");
        }
    }
}
