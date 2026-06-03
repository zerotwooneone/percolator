using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddListeningPortToSelfIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PeerRoutingTlsCertificates");

            migrationBuilder.AddColumn<int>(
                name: "ListeningPort",
                table: "SelfIdentity",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ListeningPort",
                table: "SelfIdentity");

            migrationBuilder.CreateTable(
                name: "PeerRoutingTlsCertificates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AddedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RawData = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RawDataHash = table.Column<byte[]>(type: "BLOB", nullable: false)
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
                name: "IX_PeerRoutingTlsCertificates_PeerId",
                table: "PeerRoutingTlsCertificates",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "IX_PeerRoutingTlsCertificates_RawDataHash",
                table: "PeerRoutingTlsCertificates",
                column: "RawDataHash");
        }
    }
}
