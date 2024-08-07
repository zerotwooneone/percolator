using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class RemoteClient2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RemoteClientIps_RemoteClient_RemoteClientId",
                table: "RemoteClientIps");

            migrationBuilder.DropPrimaryKey(
                name: "PK_RemoteClient",
                table: "RemoteClient");

            migrationBuilder.RenameTable(
                name: "RemoteClient",
                newName: "RemoteClients");

            migrationBuilder.AddPrimaryKey(
                name: "PK_RemoteClients",
                table: "RemoteClients",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_RemoteClientIps_RemoteClients_RemoteClientId",
                table: "RemoteClientIps",
                column: "RemoteClientId",
                principalTable: "RemoteClients",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_RemoteClientIps_RemoteClients_RemoteClientId",
                table: "RemoteClientIps");

            migrationBuilder.DropPrimaryKey(
                name: "PK_RemoteClients",
                table: "RemoteClients");

            migrationBuilder.RenameTable(
                name: "RemoteClients",
                newName: "RemoteClient");

            migrationBuilder.AddPrimaryKey(
                name: "PK_RemoteClient",
                table: "RemoteClient",
                column: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_RemoteClientIps_RemoteClient_RemoteClientId",
                table: "RemoteClientIps",
                column: "RemoteClientId",
                principalTable: "RemoteClient",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
