using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyPeersTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PeerIdentityKeys_Peer_PeerId",
                table: "PeerIdentityKeys");

            migrationBuilder.DropTable(
                name: "Peers");

            migrationBuilder.AddForeignKey(
                name: "FK_PeerIdentityKeys_PeerIdentities_PeerId",
                table: "PeerIdentityKeys",
                column: "PeerId",
                principalTable: "PeerIdentities",
                principalColumn: "PeerId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PeerIdentityKeys_PeerIdentities_PeerId",
                table: "PeerIdentityKeys");

            migrationBuilder.CreateTable(
                name: "Peers",
                columns: table => new
                {
                    TempId1 = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.UniqueConstraint("AK_Peers_TempId1", x => x.TempId1);
                });

            migrationBuilder.AddForeignKey(
                name: "FK_PeerIdentityKeys_Peers_PeerId",
                table: "PeerIdentityKeys",
                column: "PeerId",
                principalTable: "Peers",
                principalColumn: "TempId1",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
