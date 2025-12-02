using System;

namespace Desktop.Wpf.Features.Shell;

public interface IIdentityScopeAccessor
{
    IServiceProvider? Current { get; set; }
}
