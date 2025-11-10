using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MovePeerConnectionsToPeerIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PeerConnections_Peers_PeerId",
                table: "PeerConnections");

            migrationBuilder.CreateTable(
                name: "PeerIdentities",
                columns: table => new
                {
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerIdentities", x => x.PeerId);
                });

            migrationBuilder.CreateTable(
                name: "PeerIdentityKeys_V2",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublicKeySpki = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Fingerprint = table.Column<byte[]>(type: "BLOB", nullable: false),
                    NotBeforeUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerIdentityKeys_V2", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PeerIdentityKeys_V2_PeerIdentities_PeerId",
                        column: x => x.PeerId,
                        principalTable: "PeerIdentities",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PeerVerifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Method = table.Column<int>(type: "INTEGER", nullable: false),
                    VerifiedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    VerifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    NotBeforeUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerVerifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PeerVerifications_PeerIdentities_PeerId",
                        column: x => x.PeerId,
                        principalTable: "PeerIdentities",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PeerIdentities_Name",
                table: "PeerIdentities",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PeerIdentityKeys_V2_Fingerprint",
                table: "PeerIdentityKeys_V2",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PeerIdentityKeys_V2_PeerId",
                table: "PeerIdentityKeys_V2",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "IX_PeerVerifications_Fingerprint",
                table: "PeerVerifications",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_PeerVerifications_PeerId_Fingerprint",
                table: "PeerVerifications",
                columns: new[] { "PeerId", "Fingerprint" });

            migrationBuilder.AddForeignKey(
                name: "FK_PeerConnections_PeerIdentities_PeerId",
                table: "PeerConnections",
                column: "PeerId",
                principalTable: "PeerIdentities",
                principalColumn: "PeerId",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PeerConnections_PeerIdentities_PeerId",
                table: "PeerConnections");

            migrationBuilder.DropTable(
                name: "PeerIdentityKeys_V2");

            migrationBuilder.DropTable(
                name: "PeerVerifications");

            migrationBuilder.DropTable(
                name: "PeerIdentities");

            migrationBuilder.AddForeignKey(
                name: "FK_PeerConnections_Peers_PeerId",
                table: "PeerConnections",
                column: "PeerId",
                principalTable: "Peers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
