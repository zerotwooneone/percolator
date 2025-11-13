using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddNetworkRoutingDomain : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiscoveredPeers",
                columns: table => new
                {
                    DiscoveryKey = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKeyHash = table.Column<byte[]>(type: "BLOB", nullable: true),
                    FirstSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    BoundPeerId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveredPeers", x => x.DiscoveryKey);
                });

            migrationBuilder.CreateTable(
                name: "PeerRoutingProfiles",
                columns: table => new
                {
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DirectMessagePublicKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ReachabilityStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    ReachabilityLastChangeUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerRoutingProfiles", x => x.PeerId);
                });

            migrationBuilder.CreateTable(
                name: "PeerRoutingGrpcEndPoints",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerRoutingGrpcEndPoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PeerRoutingGrpcEndPoints_PeerRoutingProfiles_PeerId",
                        column: x => x.PeerId,
                        principalTable: "PeerRoutingProfiles",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PeerRoutingRelays",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelayPeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerRoutingRelays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PeerRoutingRelays_PeerRoutingProfiles_PeerId",
                        column: x => x.PeerId,
                        principalTable: "PeerRoutingProfiles",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PeerRoutingTlsCertificates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RawData = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RawDataHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AddedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerRoutingTlsCertificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PeerRoutingTlsCertificates_PeerRoutingProfiles_PeerId",
                        column: x => x.PeerId,
                        principalTable: "PeerRoutingProfiles",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredPeers_PublicKeyHash",
                table: "DiscoveredPeers",
                column: "PublicKeyHash");

            migrationBuilder.CreateIndex(
                name: "IX_PeerRoutingGrpcEndPoints_PeerId_Host_Port",
                table: "PeerRoutingGrpcEndPoints",
                columns: new[] { "PeerId", "Host", "Port" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PeerRoutingRelays_PeerId_RelayPeerId",
                table: "PeerRoutingRelays",
                columns: new[] { "PeerId", "RelayPeerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PeerRoutingTlsCertificates_PeerId",
                table: "PeerRoutingTlsCertificates",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "IX_PeerRoutingTlsCertificates_RawDataHash",
                table: "PeerRoutingTlsCertificates",
                column: "RawDataHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscoveredPeers");

            migrationBuilder.DropTable(
                name: "PeerRoutingGrpcEndPoints");

            migrationBuilder.DropTable(
                name: "PeerRoutingRelays");

            migrationBuilder.DropTable(
                name: "PeerRoutingTlsCertificates");

            migrationBuilder.DropTable(
                name: "PeerRoutingProfiles");
        }
    }
}
