using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSelfIdentityKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SelfIdentityKeys",
                columns: table => new
                {
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    IdentitySigningKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    IdentityAgreementKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SignedPreKey = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SelfIdentityKeys", x => x.SelfIdentityId);
                    table.ForeignKey(
                        name: "FK_SelfIdentityKeys_SelfIdentity_SelfIdentityId",
                        column: x => x.SelfIdentityId,
                        principalTable: "SelfIdentity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SelfIdentityKeys");
        }
    }
}
