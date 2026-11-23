using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskOrganizer.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class SuggestionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "AssumedUnsizedHours",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "DueSoonWorkingDays",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FocusSharePercent",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "Settings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "AssumedUnsizedHours", "DueSoonWorkingDays", "FocusSharePercent" },
                values: new object[] { 1.0, 3, 70 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssumedUnsizedHours",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "DueSoonWorkingDays",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "FocusSharePercent",
                table: "Settings");
        }
    }
}
