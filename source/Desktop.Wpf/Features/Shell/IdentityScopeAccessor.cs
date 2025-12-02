using System;

namespace Desktop.Wpf.Features.Shell;

public sealed class IdentityScopeAccessor : IIdentityScopeAccessor
{
    public IServiceProvider? Current { get; set; }
}
