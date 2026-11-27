using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskOrganizer.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class Integrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalUrl",
                table: "Tasks",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AzureDevOpsOrganizationUrl",
                table: "Settings",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AzureDevOpsProject",
                table: "Settings",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JiraEmail",
                table: "Settings",
                type: "TEXT",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JiraSiteUrl",
                table: "Settings",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "PullRequestDefaultHours",
                table: "Settings",
                type: "REAL",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestDefaultPriority",
                table: "Settings",
                type: "TEXT",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PullRequestProjectId",
                table: "Settings",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "JiraProjectMappings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false, collation: "NOCASE"),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JiraProjectMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_JiraProjectMappings_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "Settings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "AzureDevOpsOrganizationUrl", "AzureDevOpsProject", "JiraEmail", "JiraSiteUrl", "PullRequestDefaultHours", "PullRequestDefaultPriority", "PullRequestProjectId" },
                values: new object[] { null, null, null, null, 0.5, "High", null });

            migrationBuilder.CreateIndex(
                name: "IX_JiraProjectMappings_Prefix",
                table: "JiraProjectMappings",
                column: "Prefix",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JiraProjectMappings_ProjectId",
                table: "JiraProjectMappings",
                column: "ProjectId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JiraProjectMappings");

            migrationBuilder.DropColumn(
                name: "ExternalUrl",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "AzureDevOpsOrganizationUrl",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "AzureDevOpsProject",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "JiraEmail",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "JiraSiteUrl",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PullRequestDefaultHours",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PullRequestDefaultPriority",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "PullRequestProjectId",
                table: "Settings");
        }
    }
}
