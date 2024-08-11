using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Desktop.Migrations
{
    /// <inheritdoc />
    public partial class selfnickname : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreferredNickname",
                table: "SelfRows",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PreferredNickname",
                table: "SelfRows");
        }
    }
}
