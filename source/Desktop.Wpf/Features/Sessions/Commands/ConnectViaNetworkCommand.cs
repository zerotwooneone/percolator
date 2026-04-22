using MediatR;
using System;
using Percolator.Network;

namespace Desktop.Wpf.Features.Sessions.Commands;

public record ConnectViaNetworkCommand(
    string RouteMode,
    string? DirectEndpoint,
    string? TargetPkhText,
    PeerId? RelayHostPeerId,
    string? TargetDisplayName) : IRequest<ConnectViaNetworkResult>;

public abstract record ConnectViaNetworkResult
{
    public sealed record Success : ConnectViaNetworkResult;
    public sealed record TargetOffline : ConnectViaNetworkResult;
    public sealed record Failed(string ErrorMessage) : ConnectViaNetworkResult;
}
