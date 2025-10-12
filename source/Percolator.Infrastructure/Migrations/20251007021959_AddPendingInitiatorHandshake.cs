using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPendingInitiatorHandshake : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ActingAdminPeerId",
                table: "GroupAdminOps",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "KeyAdoptionConfirmations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    KeyVersion = table.Column<uint>(type: "INTEGER", nullable: false),
                    AdopterIdentityKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Signature = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SentAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KeyAdoptionConfirmations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PendingInitiatorHandshake",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    RecipientPublicKeyHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SignedPreKeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OneTimePreKeyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SharedSecret = table.Column<byte[]>(type: "BLOB", nullable: false),
                    LocalEphemeralPrivateKeyPkcs8 = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RemoteIdentityKeySpki = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RemotePreKeySpki = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingInitiatorHandshake", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RatchetKeyIndex",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    DirectSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RatchetPublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RatchetKeyIndex", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RatchetKeyIndex_DoubleRatchetSessions_DirectSessionId_SelfIdentityId",
                        columns: x => new { x.DirectSessionId, x.SelfIdentityId },
                        principalTable: "DoubleRatchetSessions",
                        principalColumns: new[] { "SessionId", "SelfIdentityId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KeyAdoptionConfirmations_ConversationId_KeyVersion_SentAtUtc",
                table: "KeyAdoptionConfirmations",
                columns: new[] { "ConversationId", "KeyVersion", "SentAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingInitiatorHandshake_SelfIdentityId_RecipientPublicKeyHash",
                table: "PendingInitiatorHandshake",
                columns: new[] { "SelfIdentityId", "RecipientPublicKeyHash" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingInitiatorHandshake_SelfIdentityId_SignedPreKeyId_OneTimePreKeyId",
                table: "PendingInitiatorHandshake",
                columns: new[] { "SelfIdentityId", "SignedPreKeyId", "OneTimePreKeyId" });

            migrationBuilder.CreateIndex(
                name: "IX_RatchetKeyIndex_DirectSessionId_SelfIdentityId",
                table: "RatchetKeyIndex",
                columns: new[] { "DirectSessionId", "SelfIdentityId" });

            migrationBuilder.CreateIndex(
                name: "IX_RatchetKeyIndex_SelfIdentityId_RatchetPublicKey",
                table: "RatchetKeyIndex",
                columns: new[] { "SelfIdentityId", "RatchetPublicKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KeyAdoptionConfirmations");

            migrationBuilder.DropTable(
                name: "PendingInitiatorHandshake");

            migrationBuilder.DropTable(
                name: "RatchetKeyIndex");

            migrationBuilder.DropColumn(
                name: "ActingAdminPeerId",
                table: "GroupAdminOps");
        }
    }
}
