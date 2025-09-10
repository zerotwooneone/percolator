using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GroupManagerStates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupManagerStates",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StateBlob = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupManagerStates", x => x.ConversationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupManagerStates_UpdatedAtUtc",
                table: "GroupManagerStates",
                column: "UpdatedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupManagerStates");
        }
    }
}
