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
    [Migration("20260721090000_AddGstr2bSource")]
    /// <inheritdoc />
    public partial class AddGstr2bSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Provenance of each GSTR-2B row ("GSTN" / "GSTN (N files)") so a
            // genuine portal pull is distinguishable from legacy/stale rows.
            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "GSTR2BRecords",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Source",
                table: "GSTR2BRecords");
        }
    }
}
