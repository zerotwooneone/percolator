using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class selfswitches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoReplyIntroductions",
                table: "SelfRows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "BroadcastListen",
                table: "SelfRows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "BroadcastSelf",
                table: "SelfRows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IntroduceListen",
                table: "SelfRows",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoReplyIntroductions",
                table: "SelfRows");

            migrationBuilder.DropColumn(
                name: "BroadcastListen",
                table: "SelfRows");

            migrationBuilder.DropColumn(
                name: "BroadcastSelf",
                table: "SelfRows");

            migrationBuilder.DropColumn(
                name: "IntroduceListen",
                table: "SelfRows");
        }
    }
}
