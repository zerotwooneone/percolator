using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Percolator.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DhtNodes",
                columns: table => new
                {
                    Id = table.Column<byte[]>(type: "BLOB", nullable: false),
                    EndPoint = table.Column<string>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DhtNodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Peers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Peers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SelfIdentity",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SelfIdentity", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PeerConnections",
                columns: table => new
                {
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DirectMessagePublicKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    LastSeen = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerConnections", x => x.PeerId);
                    table.ForeignKey(
                        name: "FK_PeerConnections_Peers_PeerId",
                        column: x => x.PeerId,
                        principalTable: "Peers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PeerIdentityKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerIdentityKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PeerIdentityKeys_Peers_PeerId",
                        column: x => x.PeerId,
                        principalTable: "Peers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ChannelId = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversations_SelfIdentity_SelfIdentityId",
                        column: x => x.SelfIdentityId,
                        principalTable: "SelfIdentity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DoubleRatchetSessions",
                columns: table => new
                {
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    RootKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RatchetFlag = table.Column<bool>(type: "INTEGER", nullable: false),
                    SendingChainKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ReceivingChainKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    SendingCounter = table.Column<ulong>(type: "INTEGER", nullable: false),
                    ReceivingCounter = table.Column<ulong>(type: "INTEGER", nullable: false),
                    PreviousChainLength = table.Column<ulong>(type: "INTEGER", nullable: false),
                    TheirDhRatchetPublicKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    DhRatchetPrivateKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    TheirIdentityPublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DoubleRatchetSessions", x => x.SessionId);
                    table.UniqueConstraint("AK_DoubleRatchetSessions_SessionId_SelfIdentityId", x => new { x.SessionId, x.SelfIdentityId });
                    table.ForeignKey(
                        name: "FK_DoubleRatchetSessions_SelfIdentity_SelfIdentityId",
                        column: x => x.SelfIdentityId,
                        principalTable: "SelfIdentity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SelfIdentityKnownPeer",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SelfIdentityKnownPeer", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SelfIdentityKnownPeer_SelfIdentity_SelfIdentityId",
                        column: x => x.SelfIdentityId,
                        principalTable: "SelfIdentity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DirectSession",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RemotePeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectSession", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectSession_PeerConnections_RemotePeerId",
                        column: x => x.RemotePeerId,
                        principalTable: "PeerConnections",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DirectSession_SelfIdentity_SelfIdentityId",
                        column: x => x.SelfIdentityId,
                        principalTable: "SelfIdentity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GrpcEndPoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeen = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GrpcEndPoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GrpcEndPoints_PeerConnections_PeerId",
                        column: x => x.PeerId,
                        principalTable: "PeerConnections",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TlsCertificates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RawData = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RawDataHash = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TlsCertificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TlsCertificates_PeerConnections_PeerId",
                        column: x => x.PeerId,
                        principalTable: "PeerConnections",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OneTimePreKeys",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PeerIdentityKeyId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OneTimePreKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OneTimePreKeys_PeerIdentityKeys_PeerIdentityKeyId",
                        column: x => x.PeerIdentityKeyId,
                        principalTable: "PeerIdentityKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SignedPreKeys",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Signature = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PeerIdentityKeyId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SignedPreKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SignedPreKeys_PeerIdentityKeys_PeerIdentityKeyId",
                        column: x => x.PeerIdentityKeyId,
                        principalTable: "PeerIdentityKeys",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ConversationParticipants",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationParticipants", x => new { x.ConversationId, x.ParticipantId });
                    table.ForeignKey(
                        name: "FK_ConversationParticipants_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SenderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Body = table.Column<string>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Messages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SkippedMessageKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    RatchetKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    MessageNumber = table.Column<ulong>(type: "INTEGER", nullable: false),
                    MessageKey = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SkippedMessageKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SkippedMessageKeys_DoubleRatchetSessions_SessionId_SelfIdentityId",
                        columns: x => new { x.SessionId, x.SelfIdentityId },
                        principalTable: "DoubleRatchetSessions",
                        principalColumns: new[] { "SessionId", "SelfIdentityId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_SelfIdentityId_ChannelId",
                table: "Conversations",
                columns: new[] { "SelfIdentityId", "ChannelId" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectSession_RemotePeerId",
                table: "DirectSession",
                column: "RemotePeerId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectSession_SelfIdentityId_RemotePeerId",
                table: "DirectSession",
                columns: new[] { "SelfIdentityId", "RemotePeerId" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectSession_SelfIdentityId_SessionId",
                table: "DirectSession",
                columns: new[] { "SelfIdentityId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_DoubleRatchetSessions_SelfIdentityId_UpdatedAt",
                table: "DoubleRatchetSessions",
                columns: new[] { "SelfIdentityId", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_GrpcEndPoints_PeerId",
                table: "GrpcEndPoints",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_SentAt",
                table: "Messages",
                columns: new[] { "ConversationId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OneTimePreKeys_PeerIdentityKeyId",
                table: "OneTimePreKeys",
                column: "PeerIdentityKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_PeerConnections_DirectMessagePublicKey",
                table: "PeerConnections",
                column: "DirectMessagePublicKey");

            migrationBuilder.CreateIndex(
                name: "IX_PeerIdentityKeys_PeerId",
                table: "PeerIdentityKeys",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "IX_Peers_Name",
                table: "Peers",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SelfIdentity_Name",
                table: "SelfIdentity",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SelfIdentity_PeerId",
                table: "SelfIdentity",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "IX_SelfIdentityKnownPeer_SelfIdentityId_PeerId",
                table: "SelfIdentityKnownPeer",
                columns: new[] { "SelfIdentityId", "PeerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SignedPreKeys_PeerIdentityKeyId",
                table: "SignedPreKeys",
                column: "PeerIdentityKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_SkippedMessageKeys_SelfIdentityId_SessionId_RatchetKey_MessageNumber",
                table: "SkippedMessageKeys",
                columns: new[] { "SelfIdentityId", "SessionId", "RatchetKey", "MessageNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SkippedMessageKeys_SessionId_SelfIdentityId",
                table: "SkippedMessageKeys",
                columns: new[] { "SessionId", "SelfIdentityId" });

            migrationBuilder.CreateIndex(
                name: "IX_TlsCertificates_PeerId",
                table: "TlsCertificates",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "IX_TlsCertificates_RawDataHash",
                table: "TlsCertificates",
                column: "RawDataHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationParticipants");

            migrationBuilder.DropTable(
                name: "DhtNodes");

            migrationBuilder.DropTable(
                name: "DirectSession");

            migrationBuilder.DropTable(
                name: "GrpcEndPoints");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "OneTimePreKeys");

            migrationBuilder.DropTable(
                name: "SelfIdentityKnownPeer");

            migrationBuilder.DropTable(
                name: "SignedPreKeys");

            migrationBuilder.DropTable(
                name: "SkippedMessageKeys");

            migrationBuilder.DropTable(
                name: "TlsCertificates");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "PeerIdentityKeys");

            migrationBuilder.DropTable(
                name: "DoubleRatchetSessions");

            migrationBuilder.DropTable(
                name: "PeerConnections");

            migrationBuilder.DropTable(
                name: "SelfIdentity");

            migrationBuilder.DropTable(
                name: "Peers");
        }
    }
}
