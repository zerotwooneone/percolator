using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScopeExistingTablesBySelfIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop old unique indexes that were global (not scoped by SelfIdentityId)
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_DirectSession_RemotePeerId\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_DirectSession_SessionId\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Conversations_ChannelId\";");

            // Columns 'SelfIdentityId' are assumed to exist (previous attempt may have added them). We only ensure indexes below.

            // Create new scoped indexes
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_DirectSession_SelfIdentityId_RemotePeerId\" ON \"DirectSession\" (\"SelfIdentityId\", \"RemotePeerId\");");
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_DirectSession_SelfIdentityId_SessionId\" ON \"DirectSession\" (\"SelfIdentityId\", \"SessionId\");");
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_Conversations_SelfIdentityId_ChannelId\" ON \"Conversations\" (\"SelfIdentityId\", \"ChannelId\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Drop new indexes
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_DirectSession_SelfIdentityId_RemotePeerId\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_DirectSession_SelfIdentityId_SessionId\";");
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_Conversations_SelfIdentityId_ChannelId\";");

            // Drop columns
            // SQLite doesn't support DROP COLUMN prior to 3.35 table rebuild logic; to keep Down simple, we leave columns in place.
            // If a full rollback is required, perform it manually by recreating the tables without these columns.

            // Restore previous indexes
            migrationBuilder.CreateIndex(
                name: "IX_DirectSession_RemotePeerId",
                table: "DirectSession",
                column: "RemotePeerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DirectSession_SessionId",
                table: "DirectSession",
                column: "SessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_ChannelId",
                table: "Conversations",
                column: "ChannelId",
                unique: true);
        }
    }
}
