using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPeerRouteCandidates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PeerRouteCandidates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    RemotePeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RouteKind = table.Column<int>(type: "INTEGER", nullable: false),
                    EndpointHost = table.Column<string>(type: "TEXT", nullable: true),
                    EndpointPort = table.Column<int>(type: "INTEGER", nullable: true),
                    RelayHostPeerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSuccessAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerRouteCandidates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PeerRouteCandidates_SelfIdentityId_RemotePeerId_RouteKind_EndpointHost_EndpointPort_RelayHostPeerId",
                table: "PeerRouteCandidates",
                columns: new[] { "SelfIdentityId", "RemotePeerId", "RouteKind", "EndpointHost", "EndpointPort", "RelayHostPeerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PeerRouteCandidates");
        }
    }
}
