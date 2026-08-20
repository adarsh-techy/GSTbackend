using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GSTAutoPilot.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ReworkDocumentMappingsDocTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Direction",
                table: "DocumentMappings");

            migrationBuilder.RenameColumn(
                name: "DocTypeFilter",
                table: "DocumentMappings",
                newName: "SubTypes");

            migrationBuilder.AddColumn<string>(
                name: "DocTypes",
                table: "DocumentMappings",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsOutward",
                table: "DocumentMappings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "DocumentMappings",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DocTypes",
                table: "DocumentMappings");

            migrationBuilder.DropColumn(
                name: "IsOutward",
                table: "DocumentMappings");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "DocumentMappings");

            migrationBuilder.RenameColumn(
                name: "SubTypes",
                table: "DocumentMappings",
                newName: "DocTypeFilter");

            migrationBuilder.AddColumn<string>(
                name: "Direction",
                table: "DocumentMappings",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "");
        }
    }
}
