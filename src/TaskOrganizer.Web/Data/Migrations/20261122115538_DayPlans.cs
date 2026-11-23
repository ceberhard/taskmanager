using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskOrganizer.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DayPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DayPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    AcceptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    HoursAvailable = table.Column<double>(type: "REAL", nullable: false),
                    TasksSuggested = table.Column<int>(type: "INTEGER", nullable: false),
                    TasksAcceptedFromSuggestion = table.Column<int>(type: "INTEGER", nullable: false),
                    TasksSwappedIn = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DayPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DayPlanFocusProjects",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DayPlanId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DayPlanFocusProjects", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DayPlanFocusProjects_DayPlans_DayPlanId",
                        column: x => x.DayPlanId,
                        principalTable: "DayPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DayPlanFocusProjects_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DayPlanFocusProjects_DayPlanId",
                table: "DayPlanFocusProjects",
                column: "DayPlanId");

            migrationBuilder.CreateIndex(
                name: "IX_DayPlanFocusProjects_ProjectId",
                table: "DayPlanFocusProjects",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_DayPlans_Date",
                table: "DayPlans",
                column: "Date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DayPlanFocusProjects");

            migrationBuilder.DropTable(
                name: "DayPlans");
        }
    }
}
