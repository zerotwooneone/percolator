using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAckIdToMessageQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingInitiatorHandshake");

            migrationBuilder.AddColumn<Guid>(
                name: "AckId",
                table: "MessageQueue",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "PreHandshakeSessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    LocalRequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipientPublicKeyHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    InitiatorEphemeralPrivateKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    InitialRootKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RemoteIdentityKeySpki = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreHandshakeSessions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageQueue_AckId",
                table: "MessageQueue",
                column: "AckId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PreHandshakeSessions_SelfIdentityId",
                table: "PreHandshakeSessions",
                column: "SelfIdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_PreHandshakeSessions_SelfIdentityId_ExpiresAtUtc",
                table: "PreHandshakeSessions",
                columns: new[] { "SelfIdentityId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PreHandshakeSessions_SelfIdentityId_LocalRequestId",
                table: "PreHandshakeSessions",
                columns: new[] { "SelfIdentityId", "LocalRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PreHandshakeSessions_SelfIdentityId_RecipientPublicKeyHash",
                table: "PreHandshakeSessions",
                columns: new[] { "SelfIdentityId", "RecipientPublicKeyHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PreHandshakeSessions");

            migrationBuilder.DropIndex(
                name: "IX_MessageQueue_AckId",
                table: "MessageQueue");

            migrationBuilder.DropColumn(
                name: "AckId",
                table: "MessageQueue");

            migrationBuilder.CreateTable(
                name: "PendingInitiatorHandshake",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LocalEphemeralPrivateKeyPkcs8 = table.Column<byte[]>(type: "BLOB", nullable: false),
                    OneTimePreKeyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RecipientPublicKeyHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RemoteIdentityKeySpki = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RemotePreKeySpki = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    SharedSecret = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SignedPreKeyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingInitiatorHandshake", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingInitiatorHandshake_SelfIdentityId_RecipientPublicKeyHash",
                table: "PendingInitiatorHandshake",
                columns: new[] { "SelfIdentityId", "RecipientPublicKeyHash" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingInitiatorHandshake_SelfIdentityId_SignedPreKeyId_OneTimePreKeyId",
                table: "PendingInitiatorHandshake",
                columns: new[] { "SelfIdentityId", "SignedPreKeyId", "OneTimePreKeyId" });
        }
    }
}
