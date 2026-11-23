using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace TaskOrganizer.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaskHoldAndDoneForToday : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Note",
                table: "TaskStatusChanges",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmedAtUtc",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DoneForTodayAtUtc",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "HoldCheckBackOn",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "HoldReason",
                table: "Tasks",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HoldReasonSuggestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Text = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false, collation: "NOCASE")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HoldReasonSuggestions", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "HoldReasonSuggestions",
                columns: new[] { "Id", "Text" },
                values: new object[,]
                {
                    { 1, "NEEDS TESTING" },
                    { 2, "COMMENTS POSTED" },
                    { 3, "AWAITING FEEDBACK" }
                });

            migrationBuilder.CreateIndex(
                name: "IX_HoldReasonSuggestions_Text",
                table: "HoldReasonSuggestions",
                column: "Text",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HoldReasonSuggestions");

            migrationBuilder.DropColumn(
                name: "Note",
                table: "TaskStatusChanges");

            migrationBuilder.DropColumn(
                name: "ConfirmedAtUtc",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "DoneForTodayAtUtc",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "HoldCheckBackOn",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "HoldReason",
                table: "Tasks");
        }
    }
}
