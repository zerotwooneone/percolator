using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class R8_RemoveInitiatorEphemeralPrivateKey_AddSpkiHashIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InitiatorEphemeralPrivateKey",
                table: "PreHandshakeSessions");

            migrationBuilder.AddColumn<byte[]>(
                name: "RemoteIdentityKeySpkiHash",
                table: "PreHandshakeSessions",
                type: "BLOB",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PreHandshakeSessions_SelfIdentityId_RemoteIdentityKeySpkiHash_CreatedAtUtc",
                table: "PreHandshakeSessions",
                columns: new[] { "SelfIdentityId", "RemoteIdentityKeySpkiHash", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PreHandshakeSessions_SelfIdentityId_RemoteIdentityKeySpkiHash_CreatedAtUtc",
                table: "PreHandshakeSessions");

            migrationBuilder.DropColumn(
                name: "RemoteIdentityKeySpkiHash",
                table: "PreHandshakeSessions");

            migrationBuilder.AddColumn<byte[]>(
                name: "InitiatorEphemeralPrivateKey",
                table: "PreHandshakeSessions",
                type: "BLOB",
                nullable: false,
                defaultValue: new byte[0]);
        }
    }
}
