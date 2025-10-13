using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Contracts;

namespace Percolator.Application.Network
{
    // Context available from the caller (session/peer identity, etc.). Extend as needed.
    public sealed record SessionContext(Guid? SessionId, int SelfIdentityId, Guid? RemotePeerGuid);

    // Request: provide a parsed InternalEnvelope and related context.
    public sealed record ProcessInternalEnvelopeCommand(
        InternalEnvelope Envelope,
        SessionContext Context
    ) : IRequest<InternalEnvelope>;

    internal sealed class ProcessInternalEnvelopeHandler : IRequestHandler<ProcessInternalEnvelopeCommand, InternalEnvelope>
    {
        private readonly ILogger<ProcessInternalEnvelopeHandler> _logger;
        public ProcessInternalEnvelopeHandler(ILogger<ProcessInternalEnvelopeHandler> logger)
        {
            _logger = logger;
        }

        public Task<InternalEnvelope> Handle(ProcessInternalEnvelopeCommand request, CancellationToken cancellationToken)
        {
            // Thin pass-through: call sites must pre-filter allowed ApplicationPayloadCase
            _logger.LogDebug("Processing InternalEnvelope with case {Case}", request.Envelope.ApplicationPayloadCase);
            return Task.FromResult(request.Envelope);
        }
    }
}
