using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddHierarchicalKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PreKeyBundles");

            migrationBuilder.CreateTable(
                name: "PeerIdentityKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerIdentityKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PeerIdentityKeys_Peers_PeerId",
                        column: x => x.PeerId,
                        principalTable: "Peers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OneTimePreKeys",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PeerIdentityKeyId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OneTimePreKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OneTimePreKeys_PeerIdentityKeys_PeerIdentityKeyId",
                        column: x => x.PeerIdentityKeyId,
                        principalTable: "PeerIdentityKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SignedPreKeys",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Signature = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PeerIdentityKeyId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignedPreKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignedPreKeys_PeerIdentityKeys_PeerIdentityKeyId",
                        column: x => x.PeerIdentityKeyId,
                        principalTable: "PeerIdentityKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Peers_Name",
                table: "Peers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OneTimePreKeys_PeerIdentityKeyId",
                table: "OneTimePreKeys",
                column: "PeerIdentityKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_PeerIdentityKeys_PeerId",
                table: "PeerIdentityKeys",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "IX_SignedPreKeys_PeerIdentityKeyId",
                table: "SignedPreKeys",
                column: "PeerIdentityKeyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OneTimePreKeys");

            migrationBuilder.DropTable(
                name: "SignedPreKeys");

            migrationBuilder.DropTable(
                name: "PeerIdentityKeys");

            migrationBuilder.DropIndex(
                name: "IX_Peers_Name",
                table: "Peers");

            migrationBuilder.CreateTable(
                name: "PreKeyBundles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExpirationDateUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IdentityAgreementKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    IdentitySigningKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    OneTimePreKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    SignedPreKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SignedPreKeySignature = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreKeyBundles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PreKeyBundles_Peers_PeerId",
                        column: x => x.PeerId,
                        principalTable: "Peers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PreKeyBundles_PeerId",
                table: "PreKeyBundles",
                column: "PeerId");
        }
    }
}
