using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SokagimdakiSorun.Messaging.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboundComplaintReference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "complaint_reference",
                schema: "messaging",
                table: "messages",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "complaint_reference",
                schema: "messaging",
                table: "messages");
        }
    }
}
