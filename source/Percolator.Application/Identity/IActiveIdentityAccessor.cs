namespace Percolator.Application.Identity;

public interface IActiveIdentityAccessor
{
    bool IsActive { get; }
}
