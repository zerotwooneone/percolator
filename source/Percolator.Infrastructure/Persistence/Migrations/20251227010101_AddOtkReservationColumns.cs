using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Percolator.Infrastructure.Persistence;

#nullable disable

namespace Percolator.Infrastructure.Persistence.Migrations
{
    [DbContext(typeof(PercolatorDbContext))]
    [Migration("20251227010101_AddOtkReservationColumns")]
    public partial class AddOtkReservationColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ReservedForRequestCorrelationId",
                table: "SelfOneTimePreKeys",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReservedUntilUtc",
                table: "SelfOneTimePreKeys",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SelfOneTimePreKeys_SelfIdentityId_ReservedForRequestCorrelationId",
                table: "SelfOneTimePreKeys",
                columns: new[] { "SelfIdentityId", "ReservedForRequestCorrelationId" },
                unique: true,
                filter: "\"ReservedForRequestCorrelationId\" IS NOT NULL");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SelfOneTimePreKeys_SelfIdentityId_ReservedForRequestCorrelationId",
                table: "SelfOneTimePreKeys");

            migrationBuilder.DropColumn(
                name: "ReservedForRequestCorrelationId",
                table: "SelfOneTimePreKeys");

            migrationBuilder.DropColumn(
                name: "ReservedUntilUtc",
                table: "SelfOneTimePreKeys");
        }
    }
}
