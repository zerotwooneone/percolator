using MediatR;
using System;

namespace Desktop.Wpf.Features.Sessions.Commands;

public record ConnectViaNetworkCommand(
    string RouteMode,
    string? DirectEndpoint,
    string? TargetPkhText,
    Guid? RelayHostPeerId,
    string? TargetDisplayName) : IRequest<ConnectViaNetworkResult>;

public abstract record ConnectViaNetworkResult
{
    public sealed record Success : ConnectViaNetworkResult;
    public sealed record TargetOffline : ConnectViaNetworkResult;
    public sealed record Failed(string ErrorMessage) : ConnectViaNetworkResult;
}
