using System.Security;
using System.Security.Cryptography;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Percolator.Contracts.Protos;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Messaging;
using Proto = Percolator.Contracts.Protos;

namespace Percolator.Application.Messaging
{
    /// <summary>
    /// Implements the gRPC service contract for messaging and group management.
    /// </summary>
    public class MessagingGrpcService : Percolator.Contracts.Protos.Messaging.MessagingBase
    {
        private readonly IMessageService _messageService;
        private readonly IGroupService _groupService;
        private readonly IIdentityService _identityService;
        private readonly IPeerIdentityStore _peerIdentityStore;
        private readonly ILogger<MessagingGrpcService> _logger;
        private readonly X3DHManager _x3dhManager;

        public MessagingGrpcService(IMessageService messageService, IGroupService groupService, IIdentityService identityService, IPeerIdentityStore peerIdentityStore, ILogger<MessagingGrpcService> logger, X3DHManager x3dhManager)
        {
            _messageService = messageService;
            _groupService = groupService;
            _identityService = identityService;
            _peerIdentityStore = peerIdentityStore;
            _logger = logger;
            _x3dhManager = x3dhManager;
        }

        private string GetPeerId(ServerCallContext context)
        {
            var certificate = context.GetHttpContext().Connection.ClientCertificate;
            var thumbprint = certificate?.Thumbprint;
            if (string.IsNullOrEmpty(thumbprint))
            {
                _logger.LogError("Could not determine peer identity. Client certificate thumbprint is missing.");
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Client certificate thumbprint is required."));
            }
            return thumbprint;
        }

        public override Task<SendDirectMessageResponse> SendPreKeyDirectMessage(SendPreKeyDirectMessageRequest request, ServerCallContext context)
        {
            var peerId = GetPeerId(context);
            _logger.LogInformation("Received pre-key direct message from peer {peerId} for identity '{identityName}'", peerId, request.IdentityName);

            try
            {
                // 1. Retrieve the recipient's (local user's) private keys (IK, SPK, OPK).
                var localKeys = _identityService.GetIdentityKeys(request.IdentityName);

                // 2. Call X3DHManager.RespondToHandshake to compute the shared secret.
                var oneTimePreKey = localKeys.OneTimePreKey;
                if (oneTimePreKey is null)
                {
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "No one-time pre-keys available for the recipient."));
                }

                var sharedKey = _x3dhManager.RespondToHandshake(
                    request.InitiatorIdentityKey.ToByteArray(),
                    request.InitiatorEphemeralKey.ToByteArray(),
                    localKeys.IdentitySigningKey,
                    localKeys.IdentityAgreementKey,
                    localKeys.SignedPreKey,
                    oneTimePreKey);

                _logger.LogInformation("Successfully computed shared secret with peer {peerId}", peerId);

                // TODO:
                // 3. Decrypt request.encrypted_payload using the shared secret (e.g., AES-GCM).
                // 4. The decrypted payload will be the original message content.
                // 5. Create a new DoubleRatchetSession with the shared secret and store it.
                // 6. Store the initiator's identity and pre-key bundle using IPeerIdentityStore.
                // 7. Pass the decrypted message to _messageService.

                // For now, we'll just acknowledge the handshake.
                return Task.FromResult(new SendDirectMessageResponse { Success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process pre-key direct message from peer {peerId}", peerId);
                throw new RpcException(new Status(StatusCode.Internal, "An error occurred during handshake."));
            }
        }

        public override Task<PublishPreKeyBundleResponse> PublishPreKeyBundle(PublishPreKeyBundleRequest request, ServerCallContext context)
        {
            var peerId = GetPeerId(context);
            var identityKey = request.Bundle.IdentityKey.ToByteArray();
            var bundleBytes = request.Bundle.ToByteArray();

            var peerIdentity = new PeerIdentity(identityKey, bundleBytes);
            _peerIdentityStore.StorePeerAsync(peerIdentity); // Note: This is an async method but we don't await it.

            _logger.LogInformation("Stored pre-key bundle for peer {peerId} and identity '{identityName}'", peerId, request.IdentityName);

            return Task.FromResult(new PublishPreKeyBundleResponse { Success = true });
        }

        public override Task<GetPreKeyBundleResponse> GetPreKeyBundle(GetPreKeyBundleRequest request, ServerCallContext context)
        {
            try
            {
                _logger.LogInformation("GetPreKeyBundle invoked for identity '{identityName}' and user '{userId}'.", request.IdentityName, request.UserId);
                var certificate = _identityService.GetIdentityCertificate(request.IdentityName);
                if (!string.Equals(certificate.Thumbprint, request.UserId, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning("Permission denied. User ID {requestUserId} does not match thumbprint {certThumbprint} for identity '{identityName}'.", request.UserId, certificate.Thumbprint, request.IdentityName);
                    throw new RpcException(new Status(StatusCode.PermissionDenied, "User ID does not match certificate thumbprint for the requested identity."));
                }

                _logger.LogInformation("Certificate validation passed. Getting identity keys.");
                var keys = _identityService.GetIdentityKeys(request.IdentityName);
                _logger.LogInformation("Successfully retrieved identity keys.");

                var spkBytes = keys.SignedPreKey.PublicKey.ExportSubjectPublicKeyInfo();
                var signedPreKeySignature = keys.IdentitySigningKey.SignData(spkBytes, HashAlgorithmName.SHA256);
                _logger.LogInformation("Successfully signed the pre-key.");

                var ikBytes = keys.IdentitySigningKey.ExportSubjectPublicKeyInfo();
                var opkBytes = keys.OneTimePreKey.PublicKey.ExportSubjectPublicKeyInfo();
                _logger.LogInformation("Successfully exported public keys to byte arrays.");

                var bundle = new Proto.PreKeyBundle
                {
                    IdentityKey = ByteString.CopyFrom(ikBytes),
                    SignedPreKey = ByteString.CopyFrom(spkBytes),
                    OneTimePreKey = ByteString.CopyFrom(opkBytes),
                    SignedPreKeySignature = ByteString.CopyFrom(signedPreKeySignature)
                };
                _logger.LogInformation("Successfully created PreKeyBundle protobuf message. Returning response.");

                var response = new GetPreKeyBundleResponse { Bundle = bundle };
                return Task.FromResult(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in GetPreKeyBundle for identity {IdentityName}", request.IdentityName);
                throw;
            }
        }

        public override async Task<EditMessageResponse> EditDirectMessage(EditMessageRequest request, ServerCallContext context)
        {
            var editorId = GetPeerId(context);
            try
            {
                await _messageService.EditDirectMessageAsync(Guid.Parse(request.MessageId), editorId, request.NewContent);
                return new EditMessageResponse { Success = true };
            }
            catch (SecurityException ex)
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
            }
        }

        public override async Task<AnnotateMessageResponse> AnnotateDirectMessage(AnnotateMessageRequest request, ServerCallContext context)
        {
            var peerId = GetPeerId(context);
            try
            {
                await _messageService.AnnotateDirectMessageAsync(Guid.Parse(request.MessageId), peerId, request.Emoji);
                return new AnnotateMessageResponse { Success = true };
            }
            catch (ArgumentException ex)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
            }
        }

        public override async Task<RemoveAnnotationResponse> RemoveDirectMessageAnnotation(RemoveAnnotationRequest request, ServerCallContext context)
        {
            var peerId = GetPeerId(context);
            await _messageService.RemoveDirectMessageAnnotationAsync(Guid.Parse(request.MessageId), peerId, request.Emoji);
            return new RemoveAnnotationResponse { Success = true };
        }

        public override async Task<SendGroupMessageResponse> SendGroupMessage(SendGroupMessageRequest request, ServerCallContext context)
        {
            var senderId = GetPeerId(context);
            try
            {
                await _messageService.SendGroupMessageAsync(Guid.Parse(request.GroupId), senderId, request.Content);
                return new SendGroupMessageResponse { MessageId = Guid.NewGuid().ToString(), TimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow) };
            }
            catch (SecurityException ex)
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
            }
        }

        public override async Task<EditMessageResponse> EditGroupMessage(EditMessageRequest request, ServerCallContext context)
        {
            var editorId = GetPeerId(context);
            try
            {
                await _messageService.EditGroupMessageAsync(Guid.Parse(request.MessageId), editorId, request.NewContent);
                return new EditMessageResponse { Success = true };
            }
            catch (SecurityException ex)
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
            }
        }

        public override async Task<AnnotateMessageResponse> AnnotateGroupMessage(AnnotateMessageRequest request, ServerCallContext context)
        {
            var peerId = GetPeerId(context);
            try
            {
                await _messageService.AnnotateGroupMessageAsync(Guid.Parse(request.MessageId), peerId, request.Emoji);
                return new AnnotateMessageResponse { Success = true };
            }
            catch (ArgumentException ex)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
            }
            catch (SecurityException ex)
            {
                throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                throw new RpcException(new Status(StatusCode.NotFound, ex.Message));
            }
        }

        public override async Task<RemoveAnnotationResponse> RemoveGroupMessageAnnotation(RemoveAnnotationRequest request, ServerCallContext context)
        {
            var peerId = GetPeerId(context);
            await _messageService.RemoveGroupMessageAnnotationAsync(Guid.Parse(request.MessageId), peerId, request.Emoji);
            return new RemoveAnnotationResponse { Success = true };
        }

        public override async Task<CreateGroupResponse> CreateGroup(CreateGroupRequest request, ServerCallContext context)
        {
            var peerId = GetPeerId(context);
            var memberIds = new List<string>(request.MemberIds) { peerId };
            var group = await _groupService.CreateGroupAsync(request.Name, memberIds.Distinct());

            var response = new CreateGroupResponse
            {
                Group = new Proto.Group
                {
                    GroupId = group.Id.ToString(),
                    Name = group.Name
                }
            };
            response.Group.MemberIds.AddRange(group.MemberIds);
            return response;
        }

        public override async Task<UpdateGroupMemberResponse> AddGroupMember(UpdateGroupMemberRequest request, ServerCallContext context)
        {
            var peerId = GetPeerId(context);
            await _groupService.AddMemberToGroupAsync(Guid.Parse(request.GroupId), peerId, request.MemberId);
            return new UpdateGroupMemberResponse { Success = true };
        }

        public override async Task<UpdateGroupMemberResponse> RemoveGroupMember(UpdateGroupMemberRequest request, ServerCallContext context)
        {
            var peerId = GetPeerId(context);
            await _groupService.RemoveMemberFromGroupAsync(Guid.Parse(request.GroupId), peerId, request.MemberId);
            return new UpdateGroupMemberResponse { Success = true };
        }

        public override async Task<RenameGroupResponse> RenameGroup(RenameGroupRequest request, ServerCallContext context)
        {
            var peerId = GetPeerId(context);
            await _groupService.RenameGroupAsync(Guid.Parse(request.GroupId), peerId, request.NewName);
            return new RenameGroupResponse { Success = true };
        }

        public override async Task<Proto.Group> GetGroupDetails(GetGroupDetailsRequest request, ServerCallContext context)
        {
            var group = await _groupService.GetGroupDetailsAsync(Guid.Parse(request.GroupId));
            if (group is null)
            {
                throw new RpcException(new Status(StatusCode.NotFound, $"Group with ID {request.GroupId} not found."));
            }

            var response = new Proto.Group { GroupId = group.Id.ToString(), Name = group.Name };
            response.MemberIds.AddRange(group.MemberIds);
            return response;
        }
    }
}
