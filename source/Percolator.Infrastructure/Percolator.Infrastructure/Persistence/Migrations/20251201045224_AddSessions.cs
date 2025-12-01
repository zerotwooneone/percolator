using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Percolator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RemotePeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    RootKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SendChainKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    SendCounter = table.Column<ulong>(type: "INTEGER", nullable: false),
                    RecvChainKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    RecvCounter = table.Column<ulong>(type: "INTEGER", nullable: false),
                    PrevChainLength = table.Column<ulong>(type: "INTEGER", nullable: false),
                    RemoteRatchetKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    DhRatchetPrivateKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    AssociatedData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastUsedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => new { x.SessionId, x.SelfIdentityId });
                    table.ForeignKey(
                        name: "FK_Sessions_SelfIdentity_SelfIdentityId",
                        column: x => x.SelfIdentityId,
                        principalTable: "SelfIdentity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_SelfIdentityId_RemotePeerId",
                table: "Sessions",
                columns: new[] { "SelfIdentityId", "RemotePeerId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Sessions");
        }
    }
}
