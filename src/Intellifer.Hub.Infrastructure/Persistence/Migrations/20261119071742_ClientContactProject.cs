using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Intellifer.Hub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ClientContactProject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "customer_partner_id",
                table: "conversations",
                newName: "customer_contact_id");

            migrationBuilder.AddColumn<string>(
                name: "customer_project",
                table: "conversations",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);
        
            // The old ids were res.partner ids, not client.contact ids. Clear them; the next message looks them up again.
            migrationBuilder.Sql(
                "UPDATE conversations SET customer_contact_id = NULL, customer_name = NULL, customer_company = NULL, customer_checked_at = NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "customer_project",
                table: "conversations");

            migrationBuilder.RenameColumn(
                name: "customer_contact_id",
                table: "conversations",
                newName: "customer_partner_id");
        }
    }
}
