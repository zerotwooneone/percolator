using Microsoft.EntityFrameworkCore.Migrations;

namespace Percolator.Infrastructure.Migrations
{
    public partial class RemovePendingInitiatorHandshake : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PendingInitiatorHandshake");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PendingInitiatorHandshake",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OneTimePreKeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipientPublicKeyHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    SignedPreKeyId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingInitiatorHandshake", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PendingInitiatorHandshake_SelfIdentityId_RecipientPublicKeyHash",
                table: "PendingInitiatorHandshake",
                columns: new[] { "SelfIdentityId", "RecipientPublicKeyHash" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingInitiatorHandshake_SelfIdentityId_SignedPreKeyId_OneTimePreKeyId",
                table: "PendingInitiatorHandshake",
                columns: new[] { "SelfIdentityId", "SignedPreKeyId", "OneTimePreKeyId" });
        }
    }
}
