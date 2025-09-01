using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSelfIdentityPartitionToDoubleRatchet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Add SelfIdentityId to DoubleRatchetSessions
            migrationBuilder.AddColumn<int>(
                name: "SelfIdentityId",
                table: "DoubleRatchetSessions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Create unique alternate key (SessionId, SelfIdentityId)
            migrationBuilder.AddUniqueConstraint(
                name: "AK_DoubleRatchetSessions_SessionId_SelfIdentityId",
                table: "DoubleRatchetSessions",
                columns: new[] { "SessionId", "SelfIdentityId" });

            // Index for common lookups
            migrationBuilder.CreateIndex(
                name: "IX_DoubleRatchetSessions_SelfIdentityId_UpdatedAt",
                table: "DoubleRatchetSessions",
                columns: new[] { "SelfIdentityId", "UpdatedAt" });

            // FK to SelfIdentity
            migrationBuilder.AddForeignKey(
                name: "FK_DoubleRatchetSessions_SelfIdentity_SelfIdentityId",
                table: "DoubleRatchetSessions",
                column: "SelfIdentityId",
                principalTable: "SelfIdentity",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            // SkippedMessageKeys: add SelfIdentityId
            migrationBuilder.AddColumn<int>(
                name: "SelfIdentityId",
                table: "SkippedMessageKeys",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // Replace unique index to include SelfIdentityId
            migrationBuilder.DropIndex(
                name: "IX_SkippedMessageKeys_SessionId_RatchetKey_MessageNumber",
                table: "SkippedMessageKeys");

            migrationBuilder.CreateIndex(
                name: "IX_SkippedMessageKeys_SelfIdentityId_SessionId_RatchetKey_MessageNumber",
                table: "SkippedMessageKeys",
                columns: new[] { "SelfIdentityId", "SessionId", "RatchetKey", "MessageNumber" },
                unique: true);

            // Adjust FK from SkippedMessageKeys to DoubleRatchetSessions to composite
            migrationBuilder.DropForeignKey(
                name: "FK_SkippedMessageKeys_DoubleRatchetSessions_SessionId",
                table: "SkippedMessageKeys");

            migrationBuilder.AddForeignKey(
                name: "FK_SkippedMessageKeys_DoubleRatchetSessions_SessionId_SelfIdentityId",
                table: "SkippedMessageKeys",
                columns: new[] { "SessionId", "SelfIdentityId" },
                principalTable: "DoubleRatchetSessions",
                principalColumns: new[] { "SessionId", "SelfIdentityId" },
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Revert SkippedMessageKeys composite FK
            migrationBuilder.DropForeignKey(
                name: "FK_SkippedMessageKeys_DoubleRatchetSessions_SessionId_SelfIdentityId",
                table: "SkippedMessageKeys");

            // Drop new SkippedMessageKeys index and SelfIdentityId column
            migrationBuilder.DropIndex(
                name: "IX_SkippedMessageKeys_SelfIdentityId_SessionId_RatchetKey_MessageNumber",
                table: "SkippedMessageKeys");

            migrationBuilder.DropColumn(
                name: "SelfIdentityId",
                table: "SkippedMessageKeys");

            // Restore old unique index
            migrationBuilder.CreateIndex(
                name: "IX_SkippedMessageKeys_SessionId_RatchetKey_MessageNumber",
                table: "SkippedMessageKeys",
                columns: new[] { "SessionId", "RatchetKey", "MessageNumber" },
                unique: true);

            // Drop DoubleRatchetSessions FKs and indexes then column
            migrationBuilder.DropForeignKey(
                name: "FK_DoubleRatchetSessions_SelfIdentity_SelfIdentityId",
                table: "DoubleRatchetSessions");

            migrationBuilder.DropIndex(
                name: "IX_DoubleRatchetSessions_SelfIdentityId_UpdatedAt",
                table: "DoubleRatchetSessions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_DoubleRatchetSessions_SessionId_SelfIdentityId",
                table: "DoubleRatchetSessions");

            migrationBuilder.DropColumn(
                name: "SelfIdentityId",
                table: "DoubleRatchetSessions");

            // Restore original FK from SkippedMessageKeys to DoubleRatchetSessions by SessionId
            migrationBuilder.AddForeignKey(
                name: "FK_SkippedMessageKeys_DoubleRatchetSessions_SessionId",
                table: "SkippedMessageKeys",
                column: "SessionId",
                principalTable: "DoubleRatchetSessions",
                principalColumn: "SessionId",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
