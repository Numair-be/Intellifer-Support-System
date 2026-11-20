using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intellifer.Hub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AppErrors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "apps",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    project = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    key_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    key_hint = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_apps", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "app_errors",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    app_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    level = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    occurrences = table.Column<int>(type: "integer", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    plane_state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    plane_attempts = table.Column<int>(type: "integer", nullable: false),
                    plane_next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    plane_error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    plane_project_id = table.Column<Guid>(type: "uuid", nullable: true),
                    plane_work_item_id = table.Column<Guid>(type: "uuid", nullable: true),
                    plane_sequence_id = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_errors", x => x.id);
                    table.ForeignKey(
                        name: "fk_app_errors_apps_app_id",
                        column: x => x.app_id,
                        principalTable: "apps",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_app_errors_queue",
                table: "app_errors",
                columns: new[] { "plane_state", "plane_next_attempt_at" },
                filter: "plane_state = 'Pending'");

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_errors");

            migrationBuilder.DropTable(
                name: "apps");
        }
    }
}
