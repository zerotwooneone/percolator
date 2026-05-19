using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RenameEncryptedGroupMasterKeyBytesToGroupMasterKeyBytes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "EncryptedGroupMasterKeyBytes",
                table: "GroupCryptoStates",
                newName: "GroupMasterKeyBytes");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "GroupMasterKeyBytes",
                table: "GroupCryptoStates",
                newName: "EncryptedGroupMasterKeyBytes");
        }
    }
}
