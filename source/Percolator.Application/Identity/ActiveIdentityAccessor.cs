using System;

namespace Percolator.Application.Identity;

public sealed class ActiveIdentityAccessor : IActiveIdentityAccessor
{
    private readonly ActiveIdentityContext _context;

    public ActiveIdentityAccessor(ActiveIdentityContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public bool IsActive => _context.Identity is not null;
}
