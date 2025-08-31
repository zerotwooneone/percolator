using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Identity;
using Percolator.Identity;

namespace Percolator.Application.Cli;

public class CreateSelfIdentityHandler : IRequestHandler<CreateSelfIdentityCommand, int>
{
    private readonly ISelfIdentityRepository _selfIdentityRepository;
    private readonly IKeyManagementService _keyManagementService;
    private readonly ILogger<CreateSelfIdentityHandler> _logger;

    public CreateSelfIdentityHandler(
        ISelfIdentityRepository selfIdentityRepository,
        IKeyManagementService keyManagementService,
        ILogger<CreateSelfIdentityHandler> logger)
    {
        _selfIdentityRepository = selfIdentityRepository;
        _keyManagementService = keyManagementService;
        _logger = logger;
    }

    public async Task<int> Handle(CreateSelfIdentityCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new ArgumentException("Name is required", nameof(request.Name));
        }

        var peerId = request.PeerId ?? Guid.NewGuid();
        var id = await _selfIdentityRepository.CreateAsync(peerId, request.Name);

        var keys = await _keyManagementService.GetKeysAsync(request.Name);
        if (keys is null)
        {
            await _keyManagementService.CreateKeysAsync(request.Name);
            _logger.LogInformation("Created keys for identity '{Name}' (SelfIdentityId={Id}, PeerId={PeerId})", request.Name, id, peerId);
        }
        else
        {
            _logger.LogInformation("Identity '{Name}' (SelfIdentityId={Id}, PeerId={PeerId}) already has keys", request.Name, id, peerId);
        }

        return id;
    }
}
