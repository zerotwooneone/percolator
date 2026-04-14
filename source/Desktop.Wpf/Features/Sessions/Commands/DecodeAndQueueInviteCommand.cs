using MediatR;

namespace Desktop.Wpf.Features.Sessions.Commands;

public record DecodeAndQueueInviteCommand(string Token) : IRequest<DecodeAndQueueInviteResult>;

public abstract record DecodeAndQueueInviteResult
{
    public sealed record Success : DecodeAndQueueInviteResult;
    public sealed record Failed(string ErrorMessage) : DecodeAndQueueInviteResult;
}
