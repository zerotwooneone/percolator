using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Prekeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PeerPublicSigningKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PublicKeyHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ActiveAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerPublicSigningKeys", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PeerPublicSigningKeys_PeerId",
                table: "PeerPublicSigningKeys",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "IX_PeerPublicSigningKeys_PeerId_ActiveAtUtc",
                table: "PeerPublicSigningKeys",
                columns: new[] { "PeerId", "ActiveAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PeerPublicSigningKeys_PeerId_ExpiredAtUtc",
                table: "PeerPublicSigningKeys",
                columns: new[] { "PeerId", "ExpiredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PeerPublicSigningKeys_PublicKeyHash",
                table: "PeerPublicSigningKeys",
                column: "PublicKeyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PeerPublicSigningKeys");
        }
    }
}
