using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPreKeyBundleTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PreKeyBundles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdentitySigningKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    IdentityAgreementKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SignedPreKeySignature = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SignedPreKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    OneTimePreKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ExpirationDateUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PreKeyBundles");
        }
    }
}
