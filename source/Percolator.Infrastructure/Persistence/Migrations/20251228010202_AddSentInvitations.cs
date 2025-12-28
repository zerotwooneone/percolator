using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Percolator.Infrastructure.Persistence;

#nullable disable

namespace Percolator.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(PercolatorDbContext))]
    [Migration("20251228010202_AddSentInvitations")]
    public partial class AddSentInvitations : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SentInvitations",
                columns: table => new
                {
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestCorrelationId = table.Column<string>(type: "TEXT", nullable: false),
                    SignedPreKeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OneTimePreKeyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetPeerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentInvitations", x => new { x.SelfIdentityId, x.RequestCorrelationId });
                    table.ForeignKey(
                        name: "FK_SentInvitations_SelfIdentity_SelfIdentityId",
                        column: x => x.SelfIdentityId,
                        principalTable: "SelfIdentity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SentInvitations_SelfIdentityId_ExpiresAtUtc",
                table: "SentInvitations",
                columns: new[] { "SelfIdentityId", "ExpiresAtUtc" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SentInvitations");
        }
    }
}
