using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Percolator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Chunk1_IdentityScopedAlignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PendingSessions_SelfIdentityId_RemotePeerId",
                table: "PendingSessions",
                columns: new[] { "SelfIdentityId", "RemotePeerId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PendingSessions_SelfIdentityId_RemotePeerId",
                table: "PendingSessions");
        }
    }
}
