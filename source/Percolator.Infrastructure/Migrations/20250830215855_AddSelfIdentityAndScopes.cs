using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSelfIdentityAndScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SelfIdentity",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SelfIdentity", x => x.Id);
                });

            // Seed a default self identity so subsequent migrations can reference Id=1
            migrationBuilder.InsertData(
                table: "SelfIdentity",
                columns: new[] { "Id", "PeerId", "Name" },
                values: new object[] { 1, new Guid("fab00000-0000-0000-0000-000000000000"), "default" }
            );

            migrationBuilder.CreateTable(
                name: "SelfIdentityKnownPeer",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SelfIdentityKnownPeer", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SelfIdentityKnownPeer_SelfIdentity_SelfIdentityId",
                        column: x => x.SelfIdentityId,
                        principalTable: "SelfIdentity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SelfIdentity_Name",
                table: "SelfIdentity",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SelfIdentity_PeerId",
                table: "SelfIdentity",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "IX_SelfIdentityKnownPeer_SelfIdentityId_PeerId",
                table: "SelfIdentityKnownPeer",
                columns: new[] { "SelfIdentityId", "PeerId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SelfIdentityKnownPeer");

            migrationBuilder.DropTable(
                name: "SelfIdentity");
        }
    }
}
