using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intellifer.Hub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PlaneIntake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "plane_attempts",
                table: "messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "plane_error",
                table: "messages",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "plane_next_attempt_at",
                table: "messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "plane_sent_at",
                table: "messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "plane_state",
                table: "messages",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "plane_intake_id",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "plane_sequence_id",
                table: "conversations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "plane_tracked",
                table: "conversations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "plane_work_item_id",
                table: "conversations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_plane_queue",
                table: "messages",
                columns: new[] { "plane_state", "plane_next_attempt_at" },
                filter: "plane_state = 'Pending'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_messages_plane_queue",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "plane_attempts",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "plane_error",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "plane_next_attempt_at",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "plane_sent_at",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "plane_state",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "plane_intake_id",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "plane_sequence_id",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "plane_tracked",
                table: "conversations");

            migrationBuilder.DropColumn(
                name: "plane_work_item_id",
                table: "conversations");
        }
    }
}
