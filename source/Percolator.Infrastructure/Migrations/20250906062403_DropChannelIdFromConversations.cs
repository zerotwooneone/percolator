using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropChannelIdFromConversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Conversations_SelfIdentityId_ChannelId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ChannelId",
                table: "Conversations");

            migrationBuilder.AlterColumn<int>(
                name: "Id",
                table: "Messages",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT")
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddColumn<Guid>(
                name: "MessageGuid",
                table: "Messages",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "GroupConversationGuid",
                table: "Conversations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DeliveredReceipts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageGuid = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConversationId1 = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveredReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeliveredReceipts_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeliveredReceipts_Conversations_ConversationId1",
                        column: x => x.ConversationId1,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DirectSessionConversations",
                columns: table => new
                {
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    DirectSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectSessionConversations", x => new { x.SelfIdentityId, x.DirectSessionId });
                });

            migrationBuilder.CreateTable(
                name: "EmojiReactions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageGuid = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReactorId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Emoji = table.Column<string>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConversationId1 = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmojiReactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmojiReactions_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EmojiReactions_Conversations_ConversationId1",
                        column: x => x.ConversationId1,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReadReceipts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageGuid = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReaderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ConversationId1 = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReadReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReadReceipts_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ReadReceipts_Conversations_ConversationId1",
                        column: x => x.ConversationId1,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_MessageGuid",
                table: "Messages",
                columns: new[] { "ConversationId", "MessageGuid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_SelfIdentityId_GroupConversationGuid",
                table: "Conversations",
                columns: new[] { "SelfIdentityId", "GroupConversationGuid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveredReceipts_ConversationId_MessageGuid_RecipientId",
                table: "DeliveredReceipts",
                columns: new[] { "ConversationId", "MessageGuid", "RecipientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeliveredReceipts_ConversationId1",
                table: "DeliveredReceipts",
                column: "ConversationId1");

            migrationBuilder.CreateIndex(
                name: "IX_DirectSessionConversations_SelfIdentityId_ConversationId",
                table: "DirectSessionConversations",
                columns: new[] { "SelfIdentityId", "ConversationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmojiReactions_ConversationId_MessageGuid_ReactorId_Emoji",
                table: "EmojiReactions",
                columns: new[] { "ConversationId", "MessageGuid", "ReactorId", "Emoji" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EmojiReactions_ConversationId1",
                table: "EmojiReactions",
                column: "ConversationId1");

            migrationBuilder.CreateIndex(
                name: "IX_ReadReceipts_ConversationId_MessageGuid_ReaderId",
                table: "ReadReceipts",
                columns: new[] { "ConversationId", "MessageGuid", "ReaderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReadReceipts_ConversationId1",
                table: "ReadReceipts",
                column: "ConversationId1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DeliveredReceipts");

            migrationBuilder.DropTable(
                name: "DirectSessionConversations");

            migrationBuilder.DropTable(
                name: "EmojiReactions");

            migrationBuilder.DropTable(
                name: "ReadReceipts");

            migrationBuilder.DropIndex(
                name: "IX_Messages_ConversationId_MessageGuid",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_SelfIdentityId_GroupConversationGuid",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "MessageGuid",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "GroupConversationGuid",
                table: "Conversations");

            migrationBuilder.AlterColumn<Guid>(
                name: "Id",
                table: "Messages",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER")
                .OldAnnotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ChannelId",
                table: "Conversations",
                type: "BLOB",
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_SelfIdentityId_ChannelId",
                table: "Conversations",
                columns: new[] { "SelfIdentityId", "ChannelId" });
        }
    }
}
