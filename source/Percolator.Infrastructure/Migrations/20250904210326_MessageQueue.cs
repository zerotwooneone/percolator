using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MessageQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MessageQueue",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipientPeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Blob = table.Column<byte[]>(type: "BLOB", nullable: false),
                    EnqueuedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageQueue", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MessageQueue_EnqueuedAtUtc",
                table: "MessageQueue",
                column: "EnqueuedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MessageQueue_RecipientPeerId",
                table: "MessageQueue",
                column: "RecipientPeerId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageQueue_RecipientPeerId_EnqueuedAtUtc",
                table: "MessageQueue",
                columns: new[] { "RecipientPeerId", "EnqueuedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MessageQueue");
        }
    }
}
