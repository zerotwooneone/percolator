using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class GroupAdminState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupAdminStates",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    NextAdminSequenceNumber = table.Column<ulong>(type: "INTEGER", nullable: false),
                    LastCommittedKeyVersion = table.Column<uint>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupAdminStates", x => x.ConversationId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupAdminStates");
        }
    }
}
