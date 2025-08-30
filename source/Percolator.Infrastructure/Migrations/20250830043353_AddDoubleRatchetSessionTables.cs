using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddDoubleRatchetSessionTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DirectSession",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RemotePeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectSession", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectSession_PeerConnections_RemotePeerId",
                        column: x => x.RemotePeerId,
                        principalTable: "PeerConnections",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DoubleRatchetSessions",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RootKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RatchetFlag = table.Column<bool>(type: "INTEGER", nullable: false),
                    SendingChainKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ReceivingChainKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    SendingCounter = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ReceivingCounter = table.Column<ulong>(type: "INTEGER", nullable: false),
                    PreviousChainLength = table.Column<ulong>(type: "INTEGER", nullable: false),
                    TheirDhRatchetPublicKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    DhRatchetPrivateKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    TheirIdentityPublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoubleRatchetSessions", x => x.SessionId);
                });

            migrationBuilder.CreateTable(
                name: "SkippedMessageKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RatchetKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    MessageNumber = table.Column<ulong>(type: "INTEGER", nullable: false),
                    MessageKey = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkippedMessageKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SkippedMessageKeys_DoubleRatchetSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "DoubleRatchetSessions",
                        principalColumn: "SessionId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DirectSession_RemotePeerId",
                table: "DirectSession",
                column: "RemotePeerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DirectSession_SessionId",
                table: "DirectSession",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DoubleRatchetSessions_UpdatedAt",
                table: "DoubleRatchetSessions",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SkippedMessageKeys_SessionId_RatchetKey_MessageNumber",
                table: "SkippedMessageKeys",
                columns: new[] { "SessionId", "RatchetKey", "MessageNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DirectSession");

            migrationBuilder.DropTable(
                name: "SkippedMessageKeys");

            migrationBuilder.DropTable(
                name: "DoubleRatchetSessions");
        }
    }
}
