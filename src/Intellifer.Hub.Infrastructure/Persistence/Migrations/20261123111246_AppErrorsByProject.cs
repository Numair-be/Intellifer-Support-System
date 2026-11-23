using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intellifer.Hub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppErrorsByProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_app_errors_apps_app_id",
                table: "app_errors");

            migrationBuilder.DropTable(
                name: "apps");

            migrationBuilder.DropTable(
                name: "team_projects");

            migrationBuilder.DropPrimaryKey(
                name: "pk_app_errors",
                table: "app_errors");

            migrationBuilder.DropIndex(
                name: "ix_app_errors_repeat",
                table: "app_errors");

            migrationBuilder.DropColumn(
                name: "app_id",
                table: "app_errors");

            migrationBuilder.DropColumn(
                name: "level",
                table: "app_errors");

            migrationBuilder.DropColumn(
                name: "reported_project",
                table: "app_errors");

            migrationBuilder.RenameTable(
                name: "app_errors",
                newName: "app_error_reports");

            migrationBuilder.RenameIndex(
                name: "ix_app_errors_queue",
                table: "app_error_reports",
                newName: "ix_app_error_reports_queue");

            migrationBuilder.AddColumn<string>(
                name: "project",
                table: "app_error_reports",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "project_key",
                table: "app_error_reports",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ticketed_at",
                table: "app_error_reports",
                type: "timestamp with time zone",
                nullable: true);

            // Errors stored by the old per-app method have no project: never ticket them now.
            migrationBuilder.Sql("UPDATE app_error_reports SET plane_state = 'Skipped', plane_error = 'Stored before errors were grouped by project.' WHERE plane_state = 'Pending';");

            migrationBuilder.AddPrimaryKey(
                name: "pk_app_error_reports",
                table: "app_error_reports",
                column: "id");

            migrationBuilder.CreateIndex(
                name: "ix_app_error_reports_same",
                table: "app_error_reports",
                columns: new[] { "project_key", "fingerprint", "plane_state" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "pk_app_error_reports",
                table: "app_error_reports");

            migrationBuilder.DropIndex(
                name: "ix_app_error_reports_same",
                table: "app_error_reports");

            migrationBuilder.DropColumn(
                name: "project",
                table: "app_error_reports");

            migrationBuilder.DropColumn(
                name: "project_key",
                table: "app_error_reports");

            migrationBuilder.DropColumn(
                name: "ticketed_at",
                table: "app_error_reports");

            migrationBuilder.RenameTable(
                name: "app_error_reports",
                newName: "app_errors");

            migrationBuilder.RenameIndex(
                name: "ix_app_error_reports_queue",
                table: "app_errors",
                newName: "ix_app_errors_queue");

            migrationBuilder.AddColumn<Guid>(
                name: "app_id",
                table: "app_errors",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "level",
                table: "app_errors",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "reported_project",
                table: "app_errors",
                type: "text",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "pk_app_errors",
                table: "app_errors",
                column: "id");

            migrationBuilder.CreateTable(
                name: "apps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key_hint = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    last_error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    project = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_apps", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "team_projects",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_team_projects", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_app_errors_repeat",
                table: "app_errors",
                columns: new[] { "app_id", "fingerprint", "received_at" });

            migrationBuilder.CreateIndex(
                name: "ux_apps_key_hash",
                table: "apps",
                column: "key_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_apps_name",
                table: "apps",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_team_projects_name",
                table: "team_projects",
                column: "name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_app_errors_apps_app_id",
                table: "app_errors",
                column: "app_id",
                principalTable: "apps",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
