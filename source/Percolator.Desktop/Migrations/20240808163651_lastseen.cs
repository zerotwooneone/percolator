using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class lastseen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastSeenUtc",
                table: "RemoteClients",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSeenUtc",
                table: "RemoteClients");
        }
    }
}
