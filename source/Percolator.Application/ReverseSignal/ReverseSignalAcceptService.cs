using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using MediatR;
using Microsoft.Extensions.Logging;
using Percolator.Application.Network.Handshake;
using Percolator.Contracts;
using Percolator.Cryptography;
using Percolator.Application.Network;

namespace Percolator.Application.ReverseSignal
{
    public class ReverseSignalAcceptService
    {
        private readonly ILogger<ReverseSignalAcceptService> _logger;
        private readonly IPendingSessionRepository _pending;
        private readonly IMediator _mediator;
        private readonly IMessageService _messageService;

        public ReverseSignalAcceptService(
            ILogger<ReverseSignalAcceptService> logger,
            IPendingSessionRepository pending,
            IMediator mediator,
            IMessageService messageService)
        {
            _logger = logger;
            _pending = pending;
            _mediator = mediator;
            _messageService = messageService;
        }
    }
}
