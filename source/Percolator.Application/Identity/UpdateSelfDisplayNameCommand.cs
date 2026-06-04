using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Identity;
using Percolator.Identity.Model;

namespace Percolator.Application.Identity;

public sealed record UpdateSelfDisplayNameCommand(SelfId SelfId, string DisplayName) : IRequest;

public sealed class UpdateSelfDisplayNameHandler : IRequestHandler<UpdateSelfDisplayNameCommand>
{
    private readonly ILogger<UpdateSelfDisplayNameHandler> _logger;
    private readonly ISelfIdentityRepository _selfIdentityRepository;

    public UpdateSelfDisplayNameHandler(
        ILogger<UpdateSelfDisplayNameHandler> logger,
        ISelfIdentityRepository selfIdentityRepository)
    {
        _logger = logger;
        _selfIdentityRepository = selfIdentityRepository;
    }

    public async Task Handle(UpdateSelfDisplayNameCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName))
            throw new ArgumentException("Display name is required.");

        var identity = await _selfIdentityRepository.GetByIdAsync(request.SelfId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Self identity {request.SelfId} not found.");

        identity.SetDisplayName(request.DisplayName);
        await _selfIdentityRepository.SaveAsync(identity, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Updated display name for self identity {SelfId} to '{DisplayName}'", request.SelfId, request.DisplayName);
    }
}
