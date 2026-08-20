using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GSTAutoPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentMappings",
                columns: table => new
                {
                    MappingId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    GstCategory = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    HeaderTable = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    LineTable = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    DocTypeFilter = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Direction = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    TaxMode = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedOn = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedOn = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentMappings", x => x.MappingId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentMappings_TenantId_GstCategory",
                table: "DocumentMappings",
                columns: new[] { "TenantId", "GstCategory" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DocumentMappings");
        }
    }
}
