using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intellifer.Hub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TeamNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "team_notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_project = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    client_project_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    plane_project = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    plane_project_id = table.Column<Guid>(type: "uuid", nullable: false),
                    ticket_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    customer = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    customer_key = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    state = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_team_notifications", x => x.id);
                    table.ForeignKey(
                        name: "fk_team_notifications_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_team_notifications_conversation_id",
                table: "team_notifications",
                column: "conversation_id");

            migrationBuilder.CreateIndex(
                name: "ix_team_notifications_queue",
                table: "team_notifications",
                columns: new[] { "state", "next_attempt_at" },
                filter: "state = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "team_notifications");
        }
    }
}
