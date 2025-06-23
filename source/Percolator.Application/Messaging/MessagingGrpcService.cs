using Grpc.Core;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Contracts.Protos;
using Percolator.Cryptography;
using Percolator.Identity;

namespace Percolator.Application.Messaging;

public class MessagingGrpcService : Percolator.Contracts.Protos.Messaging.MessagingBase
{
    private readonly ILogger<MessagingGrpcService> _logger;
    private readonly ActiveIdentityContext _activeIdentityContext;
    private readonly IGroupManager _groupManager;

    public MessagingGrpcService(
        ILogger<MessagingGrpcService> logger,
        ActiveIdentityContext activeIdentityContext,
        IGroupManager groupManager)
    {
        _logger = logger;
        _activeIdentityContext = activeIdentityContext;
        _groupManager = groupManager;
    }

    public override Task<PublishPreKeyBundleResponse> PublishPreKeyBundle(PublishPreKeyBundleRequest request, ServerCallContext context)
    {
        return base.PublishPreKeyBundle(request, context);
    }

    public override Task<GetPreKeyBundleResponse> GetPreKeyBundle(GetPreKeyBundleRequest request, ServerCallContext context)
    {
        return base.GetPreKeyBundle(request, context);
    }

    public override Task<SendDirectMessageResponse> SendPreKeyDirectMessage(SendPreKeyDirectMessageRequest request, ServerCallContext context)
    {
        return base.SendPreKeyDirectMessage(request, context);
    }

    public override Task<SendGroupMessageResponse> SendGroupMessage(SendGroupMessageRequest request, ServerCallContext context)
    {
        return base.SendGroupMessage(request, context);
    }

    public override Task<CreateGroupResponse> CreateGroup(CreateGroupRequest request, ServerCallContext context)
    {
        return base.CreateGroup(request, context);
    }

    public override Task<UpdateGroupMemberResponse> AddGroupMember(UpdateGroupMemberRequest request, ServerCallContext context)
    { 
        return base.AddGroupMember(request, context);
    }

    public override Task<UpdateGroupMemberResponse> RemoveGroupMember(UpdateGroupMemberRequest request, ServerCallContext context)
    {
        return base.RemoveGroupMember(request, context);
    }

    public override Task<RenameGroupResponse> RenameGroup(RenameGroupRequest request, ServerCallContext context)
    {
        return base.RenameGroup(request, context);
    }

    public override Task<Group> GetGroupDetails(GetGroupDetailsRequest request, ServerCallContext context)
    {
        return base.GetGroupDetails(request, context);
    }
}
