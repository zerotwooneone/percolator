using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Percolator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyDoubleRatchetTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RatchetKeyIndex_DoubleRatchetSessions_DirectSessionId_SelfIdentityId",
                table: "RatchetKeyIndex");

            migrationBuilder.DropTable(
                name: "SkippedMessageKeys");

            migrationBuilder.DropTable(
                name: "DoubleRatchetSessions");

            migrationBuilder.DropIndex(
                name: "IX_RatchetKeyIndex_DirectSessionId_SelfIdentityId",
                table: "RatchetKeyIndex");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DoubleRatchetSessions",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DhRatchetPrivateKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    PreviousChainLength = table.Column<ulong>(type: "INTEGER", nullable: false),
                    RatchetFlag = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReceivingChainKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ReceivingCounter = table.Column<ulong>(type: "INTEGER", nullable: false),
                    RootKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    SendingChainKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    SendingCounter = table.Column<ulong>(type: "INTEGER", nullable: false),
                    TheirDhRatchetPublicKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    TheirIdentityPublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoubleRatchetSessions", x => x.SessionId);
                    table.UniqueConstraint("AK_DoubleRatchetSessions_SessionId_SelfIdentityId", x => new { x.SessionId, x.SelfIdentityId });
                    table.ForeignKey(
                        name: "FK_DoubleRatchetSessions_SelfIdentity_SelfIdentityId",
                        column: x => x.SelfIdentityId,
                        principalTable: "SelfIdentity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SkippedMessageKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    MessageKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    MessageNumber = table.Column<ulong>(type: "INTEGER", nullable: false),
                    RatchetKey = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkippedMessageKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SkippedMessageKeys_DoubleRatchetSessions_SessionId_SelfIdentityId",
                        columns: x => new { x.SessionId, x.SelfIdentityId },
                        principalTable: "DoubleRatchetSessions",
                        principalColumns: new[] { "SessionId", "SelfIdentityId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RatchetKeyIndex_DirectSessionId_SelfIdentityId",
                table: "RatchetKeyIndex",
                columns: new[] { "DirectSessionId", "SelfIdentityId" });

            migrationBuilder.CreateIndex(
                name: "IX_DoubleRatchetSessions_SelfIdentityId_UpdatedAt",
                table: "DoubleRatchetSessions",
                columns: new[] { "SelfIdentityId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SkippedMessageKeys_SelfIdentityId_SessionId_RatchetKey_MessageNumber",
                table: "SkippedMessageKeys",
                columns: new[] { "SelfIdentityId", "SessionId", "RatchetKey", "MessageNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SkippedMessageKeys_SessionId_SelfIdentityId",
                table: "SkippedMessageKeys",
                columns: new[] { "SessionId", "SelfIdentityId" });

            migrationBuilder.AddForeignKey(
                name: "FK_RatchetKeyIndex_DoubleRatchetSessions_DirectSessionId_SelfIdentityId",
                table: "RatchetKeyIndex",
                columns: new[] { "DirectSessionId", "SelfIdentityId" },
                principalTable: "DoubleRatchetSessions",
                principalColumns: new[] { "SessionId", "SelfIdentityId" },
                onDelete: ReferentialAction.Cascade);
        }
    }
}
