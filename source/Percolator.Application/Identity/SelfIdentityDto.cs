using System;

namespace Percolator.Application.Identity
{
    public sealed class SelfIdentityDto
    {
        public int Id { get; init; }
        public Guid PeerId { get; init; }
        public string Name { get; init; } = string.Empty;
    }
}
