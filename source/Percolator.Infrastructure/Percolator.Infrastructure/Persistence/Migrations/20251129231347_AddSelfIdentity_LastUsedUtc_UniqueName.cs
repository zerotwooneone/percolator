using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Percolator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSelfIdentity_LastUsedUtc_UniqueName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StateBlob",
                table: "GroupManagerStates");

            migrationBuilder.AddColumn<long>(
                name: "LastUsedUtc",
                table: "SelfIdentity",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAtUtc",
                table: "GroupManagerStates",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                table: "GroupManagerStates",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "GroupManagerStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "SequenceNumber",
                table: "GroupManagerStates",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "GroupManagerStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "GroupMembers",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MemberSpkiHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    MemberSpki = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    JoinedAtSequence = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupMembers", x => new { x.ConversationId, x.MemberSpkiHash });
                    table.ForeignKey(
                        name: "FK_GroupMembers_GroupManagerStates_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "GroupManagerStates",
                        principalColumn: "ConversationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SenderKeys",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SenderPeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChainKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SigningKey = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SenderKeys", x => new { x.ConversationId, x.SenderPeerId });
                    table.ForeignKey(
                        name: "FK_SenderKeys_GroupManagerStates_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "GroupManagerStates",
                        principalColumn: "ConversationId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SelfIdentity_LastUsedUtc",
                table: "SelfIdentity",
                column: "LastUsedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_GroupManagerStates_GroupId",
                table: "GroupManagerStates",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupMembers_ConversationId",
                table: "GroupMembers",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_SenderKeys_ConversationId",
                table: "SenderKeys",
                column: "ConversationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupMembers");

            migrationBuilder.DropTable(
                name: "SenderKeys");

            migrationBuilder.DropIndex(
                name: "IX_SelfIdentity_LastUsedUtc",
                table: "SelfIdentity");

            migrationBuilder.DropIndex(
                name: "IX_GroupManagerStates_GroupId",
                table: "GroupManagerStates");

            migrationBuilder.DropColumn(
                name: "LastUsedUtc",
                table: "SelfIdentity");

            migrationBuilder.DropColumn(
                name: "CreatedAtUtc",
                table: "GroupManagerStates");

            migrationBuilder.DropColumn(
                name: "GroupId",
                table: "GroupManagerStates");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "GroupManagerStates");

            migrationBuilder.DropColumn(
                name: "SequenceNumber",
                table: "GroupManagerStates");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "GroupManagerStates");

            migrationBuilder.AddColumn<byte[]>(
                name: "StateBlob",
                table: "GroupManagerStates",
                type: "BLOB",
                nullable: false,
                defaultValue: new byte[0]);
        }
    }
}
