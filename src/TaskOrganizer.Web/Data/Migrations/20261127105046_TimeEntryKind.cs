using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskOrganizer.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TimeEntryKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "TimeEntries",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "Work");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Kind",
                table: "TimeEntries");
        }
    }
}
