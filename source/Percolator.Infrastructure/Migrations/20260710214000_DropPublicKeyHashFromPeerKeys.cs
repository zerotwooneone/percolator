using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropPublicKeyHashFromPeerKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PeerPublicSigningKeys_PublicKeyHash",
                table: "PeerPublicSigningKeys");

            migrationBuilder.DropIndex(
                name: "IX_MessageQueue_RecipientPkh",
                table: "MessageQueue");

            migrationBuilder.DropIndex(
                name: "IX_MessageQueue_RecipientPkh_EnqueuedAtUtc",
                table: "MessageQueue");

            migrationBuilder.DropColumn(
                name: "PublicKeyHash",
                table: "PeerPublicSigningKeys");

            migrationBuilder.DropColumn(
                name: "RecipientPkh",
                table: "MessageQueue");

            migrationBuilder.AddColumn<Guid>(
                name: "InviterPublicIdentityId",
                table: "PendingSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecipientPublicIdentityId",
                table: "MessageQueue",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_MessageQueue_RecipientPublicIdentityId",
                table: "MessageQueue",
                column: "RecipientPublicIdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageQueue_RecipientPublicIdentityId_EnqueuedAtUtc",
                table: "MessageQueue",
                columns: new[] { "RecipientPublicIdentityId", "EnqueuedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MessageQueue_RecipientPublicIdentityId",
                table: "MessageQueue");

            migrationBuilder.DropIndex(
                name: "IX_MessageQueue_RecipientPublicIdentityId_EnqueuedAtUtc",
                table: "MessageQueue");

            migrationBuilder.DropColumn(
                name: "InviterPublicIdentityId",
                table: "PendingSessions");

            migrationBuilder.DropColumn(
                name: "RecipientPublicIdentityId",
                table: "MessageQueue");

            migrationBuilder.AddColumn<byte[]>(
                name: "PublicKeyHash",
                table: "PeerPublicSigningKeys",
                type: "BLOB",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.AddColumn<byte[]>(
                name: "RecipientPkh",
                table: "MessageQueue",
                type: "BLOB",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateIndex(
                name: "IX_PeerPublicSigningKeys_PublicKeyHash",
                table: "PeerPublicSigningKeys",
                column: "PublicKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageQueue_RecipientPkh",
                table: "MessageQueue",
                column: "RecipientPkh");

            migrationBuilder.CreateIndex(
                name: "IX_MessageQueue_RecipientPkh_EnqueuedAtUtc",
                table: "MessageQueue",
                columns: new[] { "RecipientPkh", "EnqueuedAtUtc" });
        }
    }
}
