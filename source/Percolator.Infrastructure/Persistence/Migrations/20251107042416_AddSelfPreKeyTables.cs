using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSelfPreKeyTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SelfOneTimePreKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    OneTimePreKeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OneTimePreKeyPrivate = table.Column<byte[]>(type: "BLOB", nullable: false),
                    OneTimePreKeyPublicSpki = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SelfOneTimePreKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SelfOneTimePreKeys_SelfIdentity_SelfIdentityId",
                        column: x => x.SelfIdentityId,
                        principalTable: "SelfIdentity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SelfPreKeySigned",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    SignedPreKeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SignedPreKeyPrivate = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SignedPreKeyPublicSpki = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PreKeySignature = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SelfPreKeySigned", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SelfPreKeySigned_SelfIdentity_SelfIdentityId",
                        column: x => x.SelfIdentityId,
                        principalTable: "SelfIdentity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SelfOneTimePreKeys_SelfIdentityId_OneTimePreKeyId",
                table: "SelfOneTimePreKeys",
                columns: new[] { "SelfIdentityId", "OneTimePreKeyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SelfPreKeySigned_SelfIdentityId_SignedPreKeyId",
                table: "SelfPreKeySigned",
                columns: new[] { "SelfIdentityId", "SignedPreKeyId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SelfOneTimePreKeys");

            migrationBuilder.DropTable(
                name: "SelfPreKeySigned");
        }
    }
}
