using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveDirectSessionPeerConnectionFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DirectSession_PeerConnections_RemotePeerId",
                table: "DirectSession");

            migrationBuilder.DropIndex(
                name: "IX_DirectSession_RemotePeerId",
                table: "DirectSession");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_DirectSession_RemotePeerId",
                table: "DirectSession",
                column: "RemotePeerId");

            migrationBuilder.AddForeignKey(
                name: "FK_DirectSession_PeerConnections_RemotePeerId",
                table: "DirectSession",
                column: "RemotePeerId",
                principalTable: "PeerConnections",
                principalColumn: "PeerId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
