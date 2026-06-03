using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemovePeerTlsCertificates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PeerRoutingTlsCertificates");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
