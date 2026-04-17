using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FixReceiptReactionConversationFk : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_DeliveredReceipts_Conversations_ConversationId1",
                table: "DeliveredReceipts");

            migrationBuilder.DropForeignKey(
                name: "FK_EmojiReactions_Conversations_ConversationId1",
                table: "EmojiReactions");

            migrationBuilder.DropForeignKey(
                name: "FK_ReadReceipts_Conversations_ConversationId1",
                table: "ReadReceipts");

            migrationBuilder.DropIndex(
                name: "IX_ReadReceipts_ConversationId1",
                table: "ReadReceipts");

            migrationBuilder.DropIndex(
                name: "IX_EmojiReactions_ConversationId1",
                table: "EmojiReactions");

            migrationBuilder.DropIndex(
                name: "IX_DeliveredReceipts_ConversationId1",
                table: "DeliveredReceipts");

            migrationBuilder.DropColumn(
                name: "ConversationId1",
                table: "ReadReceipts");

            migrationBuilder.DropColumn(
                name: "ConversationId1",
                table: "EmojiReactions");

            migrationBuilder.DropColumn(
                name: "ConversationId1",
                table: "DeliveredReceipts");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId1",
                table: "ReadReceipts",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId1",
                table: "EmojiReactions",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ConversationId1",
                table: "DeliveredReceipts",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_ReadReceipts_ConversationId1",
                table: "ReadReceipts",
                column: "ConversationId1");

            migrationBuilder.CreateIndex(
                name: "IX_EmojiReactions_ConversationId1",
                table: "EmojiReactions",
                column: "ConversationId1");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveredReceipts_ConversationId1",
                table: "DeliveredReceipts",
                column: "ConversationId1");

            migrationBuilder.AddForeignKey(
                name: "FK_DeliveredReceipts_Conversations_ConversationId1",
                table: "DeliveredReceipts",
                column: "ConversationId1",
                principalTable: "Conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_EmojiReactions_Conversations_ConversationId1",
                table: "EmojiReactions",
                column: "ConversationId1",
                principalTable: "Conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_ReadReceipts_Conversations_ConversationId1",
                table: "ReadReceipts",
                column: "ConversationId1",
                principalTable: "Conversations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
