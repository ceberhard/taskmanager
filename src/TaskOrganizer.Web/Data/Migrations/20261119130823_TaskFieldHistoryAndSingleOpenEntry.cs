using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskOrganizer.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaskFieldHistoryAndSingleOpenEntry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TaskFieldChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WorkTaskId = table.Column<int>(type: "INTEGER", nullable: false),
                    Field = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OldValue = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    NewValue = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ChangedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskFieldChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskFieldChanges_Tasks_WorkTaskId",
                        column: x => x.WorkTaskId,
                        principalTable: "Tasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TaskFieldChanges_WorkTaskId",
                table: "TaskFieldChanges",
                column: "WorkTaskId");

            // At most one time entry may be open (no end time). SQLite treats NULLs as distinct in a
            // plain unique index, so the index is on a constant, limited to the open rows.
            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX IX_TimeEntries_SingleOpen ON TimeEntries ((1)) WHERE EndTime IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_TimeEntries_SingleOpen;");

            migrationBuilder.DropTable(
                name: "TaskFieldChanges");
        }
    }
}
