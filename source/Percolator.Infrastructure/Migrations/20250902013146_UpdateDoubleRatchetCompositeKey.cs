using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class UpdateDoubleRatchetCompositeKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropUniqueConstraint(
                name: "AK_DoubleRatchetSessions_SessionId_SelfIdentityId",
                table: "DoubleRatchetSessions");

            migrationBuilder.DropPrimaryKey(
                name: "PK_DoubleRatchetSessions",
                table: "DoubleRatchetSessions");

            migrationBuilder.AddPrimaryKey(
                name: "PK_DoubleRatchetSessions",
                table: "DoubleRatchetSessions",
                columns: new[] { "SessionId", "SelfIdentityId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_DoubleRatchetSessions",
                table: "DoubleRatchetSessions");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_DoubleRatchetSessions_SessionId_SelfIdentityId",
                table: "DoubleRatchetSessions",
                columns: new[] { "SessionId", "SelfIdentityId" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_DoubleRatchetSessions",
                table: "DoubleRatchetSessions",
                column: "SessionId");
        }
    }
}
