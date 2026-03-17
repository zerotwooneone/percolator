using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSentInvitationInviteRoute : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "InviteRelayHostPeerId",
                table: "SentInvitations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "InviteRouteKind",
                table: "SentInvitations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InviteRelayHostPeerId",
                table: "SentInvitations");

            migrationBuilder.DropColumn(
                name: "InviteRouteKind",
                table: "SentInvitations");
        }
    }
}
