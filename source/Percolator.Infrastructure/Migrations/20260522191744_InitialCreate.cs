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
                name: "DiscoveredPeers",
                columns: table => new
                {
                    DiscoveryKey = table.Column<string>(type: "TEXT", nullable: false),
                    PublicKeyHash = table.Column<byte[]>(type: "BLOB", nullable: true),
                    FirstSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    BoundPeerId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveredPeers", x => x.DiscoveryKey);
                });

            migrationBuilder.CreateTable(
                name: "MessageQueue",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AckId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipientPeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Blob = table.Column<byte[]>(type: "BLOB", nullable: false),
                    EnqueuedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MessageQueue", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PeerIdentities",
                columns: table => new
                {
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerIdentities", x => x.PeerId);
                });

            migrationBuilder.CreateTable(
                name: "PeerPublicSigningKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PublicKeyHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ActiveAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiredAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerPublicSigningKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PeerRouteCandidates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    RemotePeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RouteKind = table.Column<int>(type: "INTEGER", nullable: false),
                    EndpointHost = table.Column<string>(type: "TEXT", nullable: true),
                    EndpointPort = table.Column<int>(type: "INTEGER", nullable: true),
                    RelayHostPeerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ObservedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastSuccessAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    Source = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerRouteCandidates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PeerRoutingProfiles",
                columns: table => new
                {
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DirectMessagePublicKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ReachabilityStatus = table.Column<int>(type: "INTEGER", nullable: false),
                    ReachabilityLastChangeUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerRoutingProfiles", x => x.PeerId);
                });

            migrationBuilder.CreateTable(
                name: "PendingSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    RemotePeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    Invitation = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RequestCorrelationId = table.Column<string>(type: "TEXT", nullable: true),
                    IsRelayed = table.Column<bool>(type: "INTEGER", nullable: false),
                    RelayHostPeerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    InviterIdentityKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CallbackEndpointHost = table.Column<string>(type: "TEXT", nullable: true),
                    CallbackEndpointPort = table.Column<int>(type: "INTEGER", nullable: true),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PreHandshakeSessions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    LocalRequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipientPublicKeyHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    InitialRootKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    RemoteIdentityKeySpki = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RemoteIdentityKeySpkiHash = table.Column<byte[]>(type: "BLOB", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PreHandshakeSessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RatchetKeyIndex",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    DirectSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RatchetPublicKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RatchetKeyIndex", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SelfIdentity",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    LastUsedUtc = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SelfIdentity", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiscoveredPeerEndpoints",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    DiscoveryKey = table.Column<string>(type: "TEXT", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveredPeerEndpoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoveredPeerEndpoints_DiscoveredPeers_DiscoveryKey",
                        column: x => x.DiscoveryKey,
                        principalTable: "DiscoveredPeers",
                        principalColumn: "DiscoveryKey",
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
                        name: "FK_PeerIdentityKeys_PeerIdentities_PeerId",
                        column: x => x.PeerId,
                        principalTable: "PeerIdentities",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PeerIdentityKeys_V2",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PublicKeySpki = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Fingerprint = table.Column<byte[]>(type: "BLOB", nullable: false),
                    NotBeforeUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    RevokedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerIdentityKeys_V2", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PeerIdentityKeys_V2_PeerIdentities_PeerId",
                        column: x => x.PeerId,
                        principalTable: "PeerIdentities",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PeerVerifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Fingerprint = table.Column<byte[]>(type: "BLOB", nullable: false),
                    Method = table.Column<int>(type: "INTEGER", nullable: false),
                    VerifiedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    VerifiedBy = table.Column<string>(type: "TEXT", nullable: true),
                    Note = table.Column<string>(type: "TEXT", nullable: true),
                    NotBeforeUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerVerifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PeerVerifications_PeerIdentities_PeerId",
                        column: x => x.PeerId,
                        principalTable: "PeerIdentities",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PeerRoutingGrpcEndPoints",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Port = table.Column<int>(type: "INTEGER", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerRoutingGrpcEndPoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PeerRoutingGrpcEndPoints_PeerRoutingProfiles_PeerId",
                        column: x => x.PeerId,
                        principalTable: "PeerRoutingProfiles",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PeerRoutingRelays",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RelayPeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    LastSeenUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerRoutingRelays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PeerRoutingRelays_PeerRoutingProfiles_PeerId",
                        column: x => x.PeerId,
                        principalTable: "PeerRoutingProfiles",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PeerRoutingTlsCertificates",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RawData = table.Column<byte[]>(type: "BLOB", nullable: false),
                    RawDataHash = table.Column<byte[]>(type: "BLOB", nullable: false),
                    AddedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PeerRoutingTlsCertificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PeerRoutingTlsCertificates_PeerRoutingProfiles_PeerId",
                        column: x => x.PeerId,
                        principalTable: "PeerRoutingProfiles",
                        principalColumn: "PeerId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
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
                        name: "FK_DirectSession_SelfIdentity_SelfIdentityId",
                        column: x => x.SelfIdentityId,
                        principalTable: "SelfIdentity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SelfIdentityKeys",
                columns: table => new
                {
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    IdentitySigningKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SignedPreKey = table.Column<byte[]>(type: "BLOB", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SelfIdentityKeys", x => x.SelfIdentityId);
                    table.ForeignKey(
                        name: "FK_SelfIdentityKeys_SelfIdentity_SelfIdentityId",
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
                name: "SelfOneTimePreKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    OneTimePreKeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OneTimePreKeyPrivate = table.Column<byte[]>(type: "BLOB", nullable: false),
                    OneTimePreKeyPublicSpki = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ReservedForRequestCorrelationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    ReservedUntilUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SelfOneTimePreKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SelfOneTimePreKeys_SelfIdentity_SelfIdentityId",
                        column: x => x.SelfIdentityId,
                        principalTable: "SelfIdentity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SelfPreKeySigned",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    SignedPreKeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SignedPreKeyPrivate = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SignedPreKeyPublicSpki = table.Column<byte[]>(type: "BLOB", nullable: false),
                    PreKeySignature = table.Column<byte[]>(type: "BLOB", nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SelfPreKeySigned", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SelfPreKeySigned_SelfIdentity_SelfIdentityId",
                        column: x => x.SelfIdentityId,
                        principalTable: "SelfIdentity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SentInvitations",
                columns: table => new
                {
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestCorrelationId = table.Column<string>(type: "TEXT", nullable: false),
                    SignedPreKeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OneTimePreKeyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetPeerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    TargetDisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    TargetEndpointHost = table.Column<string>(type: "TEXT", nullable: true),
                    TargetEndpointPort = table.Column<int>(type: "INTEGER", nullable: true),
                    InviteRouteKind = table.Column<int>(type: "INTEGER", nullable: false),
                    InviteRelayHostPeerId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SentInvitations", x => new { x.SelfIdentityId, x.RequestCorrelationId });
                    table.ForeignKey(
                        name: "FK_SentInvitations_SelfIdentity_SelfIdentityId",
                        column: x => x.SelfIdentityId,
                        principalTable: "SelfIdentity",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    SelfIdentityId = table.Column<int>(type: "INTEGER", nullable: false),
                    SessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RemotePeerId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ProtocolVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    RootKey = table.Column<byte[]>(type: "BLOB", nullable: false),
                    SendChainKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    SendCounter = table.Column<ulong>(type: "INTEGER", nullable: false),
                    RecvChainKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    RecvCounter = table.Column<ulong>(type: "INTEGER", nullable: false),
                    PrevChainLength = table.Column<ulong>(type: "INTEGER", nullable: false),
                    RemoteRatchetKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    DhRatchetPrivateKey = table.Column<byte[]>(type: "BLOB", nullable: true),
                    AssociatedData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastUsedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => new { x.SessionId, x.SelfIdentityId });
                    table.ForeignKey(
                        name: "FK_Sessions_SelfIdentity_SelfIdentityId",
                        column: x => x.SelfIdentityId,
                        principalTable: "SelfIdentity",
                        principalColumn: "Id",
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
                name: "DeliveredReceipts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageGuid = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecipientId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
                    SentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
                });

            migrationBuilder.CreateTable(
                name: "GroupCryptoStates",
                columns: table => new
                {
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    GroupMasterKeyBytes = table.Column<byte[]>(type: "BLOB", nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupCryptoStates", x => x.ConversationId);
                    table.ForeignKey(
                        name: "FK_GroupCryptoStates_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Messages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageGuid = table.Column<Guid>(type: "TEXT", nullable: false),
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
                name: "ReadReceipts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ConversationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MessageGuid = table.Column<Guid>(type: "TEXT", nullable: false),
                    ReaderId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
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
                });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_SelfIdentityId",
                table: "Conversations",
                column: "SelfIdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveredReceipts_ConversationId_MessageGuid_RecipientId",
                table: "DeliveredReceipts",
                columns: new[] { "ConversationId", "MessageGuid", "RecipientId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DirectSession_SelfIdentityId_RemotePeerId",
                table: "DirectSession",
                columns: new[] { "SelfIdentityId", "RemotePeerId" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectSession_SelfIdentityId_SessionId",
                table: "DirectSession",
                columns: new[] { "SelfIdentityId", "SessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectSessionConversations_SelfIdentityId_ConversationId",
                table: "DirectSessionConversations",
                columns: new[] { "SelfIdentityId", "ConversationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredPeerEndpoints_DiscoveryKey_Host_Port",
                table: "DiscoveredPeerEndpoints",
                columns: new[] { "DiscoveryKey", "Host", "Port" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredPeers_PublicKeyHash",
                table: "DiscoveredPeers",
                column: "PublicKeyHash");

            migrationBuilder.CreateIndex(
                name: "IX_EmojiReactions_ConversationId_MessageGuid_ReactorId_Emoji",
                table: "EmojiReactions",
                columns: new[] { "ConversationId", "MessageGuid", "ReactorId", "Emoji" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageQueue_AckId",
                table: "MessageQueue",
                column: "AckId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageQueue_EnqueuedAtUtc",
                table: "MessageQueue",
                column: "EnqueuedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MessageQueue_RecipientPeerId",
                table: "MessageQueue",
                column: "RecipientPeerId");

            migrationBuilder.CreateIndex(
                name: "IX_MessageQueue_RecipientPeerId_EnqueuedAtUtc",
                table: "MessageQueue",
                columns: new[] { "RecipientPeerId", "EnqueuedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_MessageGuid",
                table: "Messages",
                columns: new[] { "ConversationId", "MessageGuid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Messages_ConversationId_SentAt",
                table: "Messages",
                columns: new[] { "ConversationId", "SentAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OneTimePreKeys_PeerIdentityKeyId",
                table: "OneTimePreKeys",
                column: "PeerIdentityKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_PeerIdentities_Name",
                table: "PeerIdentities",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PeerIdentityKeys_PeerId",
                table: "PeerIdentityKeys",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "IX_PeerIdentityKeys_V2_Fingerprint",
                table: "PeerIdentityKeys_V2",
                column: "Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PeerIdentityKeys_V2_PeerId",
                table: "PeerIdentityKeys_V2",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "IX_PeerPublicSigningKeys_PeerId",
                table: "PeerPublicSigningKeys",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "IX_PeerPublicSigningKeys_PeerId_ActiveAtUtc",
                table: "PeerPublicSigningKeys",
                columns: new[] { "PeerId", "ActiveAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PeerPublicSigningKeys_PeerId_ExpiredAtUtc",
                table: "PeerPublicSigningKeys",
                columns: new[] { "PeerId", "ExpiredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PeerPublicSigningKeys_PublicKeyHash",
                table: "PeerPublicSigningKeys",
                column: "PublicKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PeerRouteCandidates_SelfIdentityId_RemotePeerId_RouteKind_EndpointHost_EndpointPort_RelayHostPeerId",
                table: "PeerRouteCandidates",
                columns: new[] { "SelfIdentityId", "RemotePeerId", "RouteKind", "EndpointHost", "EndpointPort", "RelayHostPeerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PeerRoutingGrpcEndPoints_PeerId_Host_Port",
                table: "PeerRoutingGrpcEndPoints",
                columns: new[] { "PeerId", "Host", "Port" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PeerRoutingRelays_PeerId_RelayPeerId",
                table: "PeerRoutingRelays",
                columns: new[] { "PeerId", "RelayPeerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PeerRoutingTlsCertificates_PeerId",
                table: "PeerRoutingTlsCertificates",
                column: "PeerId");

            migrationBuilder.CreateIndex(
                name: "IX_PeerRoutingTlsCertificates_RawDataHash",
                table: "PeerRoutingTlsCertificates",
                column: "RawDataHash");

            migrationBuilder.CreateIndex(
                name: "IX_PeerVerifications_Fingerprint",
                table: "PeerVerifications",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_PeerVerifications_PeerId_Fingerprint",
                table: "PeerVerifications",
                columns: new[] { "PeerId", "Fingerprint" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingSessions_SelfIdentityId_RemotePeerId",
                table: "PendingSessions",
                columns: new[] { "SelfIdentityId", "RemotePeerId" });

            migrationBuilder.CreateIndex(
                name: "IX_PreHandshakeSessions_SelfIdentityId",
                table: "PreHandshakeSessions",
                column: "SelfIdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_PreHandshakeSessions_SelfIdentityId_ExpiresAtUtc",
                table: "PreHandshakeSessions",
                columns: new[] { "SelfIdentityId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PreHandshakeSessions_SelfIdentityId_LocalRequestId",
                table: "PreHandshakeSessions",
                columns: new[] { "SelfIdentityId", "LocalRequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PreHandshakeSessions_SelfIdentityId_RecipientPublicKeyHash",
                table: "PreHandshakeSessions",
                columns: new[] { "SelfIdentityId", "RecipientPublicKeyHash" });

            migrationBuilder.CreateIndex(
                name: "IX_PreHandshakeSessions_SelfIdentityId_RemoteIdentityKeySpkiHash_CreatedAtUtc",
                table: "PreHandshakeSessions",
                columns: new[] { "SelfIdentityId", "RemoteIdentityKeySpkiHash", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_RatchetKeyIndex_SelfIdentityId_RatchetPublicKey",
                table: "RatchetKeyIndex",
                columns: new[] { "SelfIdentityId", "RatchetPublicKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReadReceipts_ConversationId_MessageGuid_ReaderId",
                table: "ReadReceipts",
                columns: new[] { "ConversationId", "MessageGuid", "ReaderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SelfIdentity_LastUsedUtc",
                table: "SelfIdentity",
                column: "LastUsedUtc");

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
                name: "IX_SelfOneTimePreKeys_SelfIdentityId_OneTimePreKeyId",
                table: "SelfOneTimePreKeys",
                columns: new[] { "SelfIdentityId", "OneTimePreKeyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SelfOneTimePreKeys_SelfIdentityId_ReservedForRequestCorrelationId",
                table: "SelfOneTimePreKeys",
                columns: new[] { "SelfIdentityId", "ReservedForRequestCorrelationId" },
                unique: true,
                filter: "\"ReservedForRequestCorrelationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_SelfPreKeySigned_SelfIdentityId_SignedPreKeyId",
                table: "SelfPreKeySigned",
                columns: new[] { "SelfIdentityId", "SignedPreKeyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SentInvitations_SelfIdentityId_ExpiresAtUtc",
                table: "SentInvitations",
                columns: new[] { "SelfIdentityId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_SelfIdentityId_RemotePeerId",
                table: "Sessions",
                columns: new[] { "SelfIdentityId", "RemotePeerId" });

            migrationBuilder.CreateIndex(
                name: "IX_SignedPreKeys_PeerIdentityKeyId",
                table: "SignedPreKeys",
                column: "PeerIdentityKeyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationParticipants");

            migrationBuilder.DropTable(
                name: "DeliveredReceipts");

            migrationBuilder.DropTable(
                name: "DhtNodes");

            migrationBuilder.DropTable(
                name: "DirectSession");

            migrationBuilder.DropTable(
                name: "DirectSessionConversations");

            migrationBuilder.DropTable(
                name: "DiscoveredPeerEndpoints");

            migrationBuilder.DropTable(
                name: "EmojiReactions");

            migrationBuilder.DropTable(
                name: "GroupCryptoStates");

            migrationBuilder.DropTable(
                name: "MessageQueue");

            migrationBuilder.DropTable(
                name: "Messages");

            migrationBuilder.DropTable(
                name: "OneTimePreKeys");

            migrationBuilder.DropTable(
                name: "PeerIdentityKeys_V2");

            migrationBuilder.DropTable(
                name: "PeerPublicSigningKeys");

            migrationBuilder.DropTable(
                name: "PeerRouteCandidates");

            migrationBuilder.DropTable(
                name: "PeerRoutingGrpcEndPoints");

            migrationBuilder.DropTable(
                name: "PeerRoutingRelays");

            migrationBuilder.DropTable(
                name: "PeerRoutingTlsCertificates");

            migrationBuilder.DropTable(
                name: "PeerVerifications");

            migrationBuilder.DropTable(
                name: "PendingSessions");

            migrationBuilder.DropTable(
                name: "PreHandshakeSessions");

            migrationBuilder.DropTable(
                name: "RatchetKeyIndex");

            migrationBuilder.DropTable(
                name: "ReadReceipts");

            migrationBuilder.DropTable(
                name: "SelfIdentityKeys");

            migrationBuilder.DropTable(
                name: "SelfIdentityKnownPeer");

            migrationBuilder.DropTable(
                name: "SelfOneTimePreKeys");

            migrationBuilder.DropTable(
                name: "SelfPreKeySigned");

            migrationBuilder.DropTable(
                name: "SentInvitations");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "SignedPreKeys");

            migrationBuilder.DropTable(
                name: "DiscoveredPeers");

            migrationBuilder.DropTable(
                name: "PeerRoutingProfiles");

            migrationBuilder.DropTable(
                name: "Conversations");

            migrationBuilder.DropTable(
                name: "PeerIdentityKeys");

            migrationBuilder.DropTable(
                name: "SelfIdentity");

            migrationBuilder.DropTable(
                name: "PeerIdentities");
        }
    }
}
