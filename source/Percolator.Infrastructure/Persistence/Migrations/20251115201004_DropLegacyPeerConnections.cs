using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DropLegacyPeerConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GrpcEndPoints");

            migrationBuilder.DropTable(
                name: "TlsCertificates");

            migrationBuilder.DropTable(
                name: "PeerConnections");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PeerConnections",
                columns: table => new
                {
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DirectMessagePublicKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    LastSeen = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RelayPeerId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerConnections", x => x.PeerId);
                    table.ForeignKey(
                        name: "FK_PeerConnections_PeerIdentities_PeerId",
                        column: x => x.PeerId,
                        principalTable: "PeerIdentities",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GrpcEndPoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    LastSeen = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrpcEndPoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GrpcEndPoints_PeerConnections_PeerId",
                        column: x => x.PeerId,
                        principalTable: "PeerConnections",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TlsCertificates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RawData = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RawDataHash = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TlsCertificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TlsCertificates_PeerConnections_PeerId",
                        column: x => x.PeerId,
                        principalTable: "PeerConnections",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GrpcEndPoints_PeerId",
                table: "GrpcEndPoints",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "IX_PeerConnections_DirectMessagePublicKey",
                table: "PeerConnections",
                column: "DirectMessagePublicKey");

            migrationBuilder.CreateIndex(
                name: "IX_TlsCertificates_PeerId",
                table: "TlsCertificates",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "IX_TlsCertificates_RawDataHash",
                table: "TlsCertificates",
                column: "RawDataHash");
        }
    }
}
