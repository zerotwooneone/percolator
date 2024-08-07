using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class RemoteClient : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Identity",
                table: "RemoteClientIps");

            migrationBuilder.AddColumn<int>(
                name: "RemoteClientId",
                table: "RemoteClientIps",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "RemoteClient",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Identity = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemoteClient", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RemoteClientIps_RemoteClientId",
                table: "RemoteClientIps",
                column: "RemoteClientId");

            migrationBuilder.AddForeignKey(
                name: "FK_RemoteClientIps_RemoteClient_RemoteClientId",
                table: "RemoteClientIps",
                column: "RemoteClientId",
                principalTable: "RemoteClient",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RemoteClientIps_RemoteClient_RemoteClientId",
                table: "RemoteClientIps");

            migrationBuilder.DropTable(
                name: "RemoteClient");

            migrationBuilder.DropIndex(
                name: "IX_RemoteClientIps_RemoteClientId",
                table: "RemoteClientIps");

            migrationBuilder.DropColumn(
                name: "RemoteClientId",
                table: "RemoteClientIps");

            migrationBuilder.AddColumn<string>(
                name: "Identity",
                table: "RemoteClientIps",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }
    }
}
