using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CompositeConversationKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ConversationParticipants_ConversationId_SelfIdentityId",
                table: "ConversationParticipants",
                columns: new[] { "ConversationId", "SelfIdentityId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ConversationParticipants_ConversationId_SelfIdentityId",
                table: "ConversationParticipants");
        }
    }
}
