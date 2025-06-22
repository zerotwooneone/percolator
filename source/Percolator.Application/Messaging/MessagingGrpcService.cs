using System;
using System.Linq;
using System.Security;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Percolator.Contracts.Protos;
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
        private readonly ILogger<MessagingGrpcService> _logger;

        public MessagingGrpcService(IMessageService messageService, IGroupService groupService, ILogger<MessagingGrpcService> logger)
        {
            _messageService = messageService;
            _groupService = groupService;
            _logger = logger;
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

        public override async Task<SendDirectMessageResponse> SendDirectMessage(SendDirectMessageRequest request, ServerCallContext context)
        {
            var senderId = GetPeerId(context);
            await _messageService.SendDirectMessageAsync(senderId, request.RecipientId, request.Content);
            return new SendDirectMessageResponse { MessageId = Guid.NewGuid().ToString(), TimestampUtc = Timestamp.FromDateTime(DateTime.UtcNow) };
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
