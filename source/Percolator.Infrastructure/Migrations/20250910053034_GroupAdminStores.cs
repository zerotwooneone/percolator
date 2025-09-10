using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GroupAdminStores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupAdminKeys",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AdminPublicKeySpki = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AddedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupAdminKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GroupAdminOps",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OpId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AppliedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupAdminOps", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GroupAdminKeys_ConversationId_AddedAtUtc",
                table: "GroupAdminKeys",
                columns: new[] { "ConversationId", "AddedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupAdminKeys_ConversationId_RevokedAtUtc",
                table: "GroupAdminKeys",
                columns: new[] { "ConversationId", "RevokedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupAdminOps_ConversationId_OpId",
                table: "GroupAdminOps",
                columns: new[] { "ConversationId", "OpId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupAdminKeys");

            migrationBuilder.DropTable(
                name: "GroupAdminOps");
        }
    }
}
