using System.Data.Common;
using Percolator.Domain.Common;
using Percolator.Domain.Security.Delegates;

namespace Percolator.Domain.Security.Ports;

/// <summary>
/// Represents an in-memory handle to the active 32-byte database encryption key.
/// Provides direct key application to unmanaged SQLite connection handles and zero-allocation span access.
/// </summary>
public interface IMasterKeyHandle : ISensitiveSecret
{
    /// <summary>
    /// Passes the raw 32-byte key span directly to the native connection (e.g. SQLitePCL.raw.sqlite3_key).
    /// Eliminates the materialization of immutable hex strings in the managed garbage collector heap.
    /// </summary>
    /// <param name=\"connection\">The database connection to key.</param>
    void ApplyKeyToConnection(DbConnection connection);

    /// <summary>
    /// Executes an action with access to the raw 32-byte key span for envelope re-wrapping or derivation.
    /// </summary>
    /// <param name=\"action\">The action to execute with the raw key span.</param>
    void WithKeySpan(Action<ReadOnlySpan<byte>> action);

    /// <summary>
    /// Executes a function with access to the raw 32-byte key span, returning a result.
    /// </summary>
    /// <typeparam name=\"TResult\">The return type of the delegate.</typeparam>
    /// <param name=\"func\">The function to execute with the raw key span.</param>
    /// <returns>The result computed by <paramref name=\"func\"/>.</returns>
    TResult WithKeySpan<TResult>(ReadOnlySpanFunc<byte, TResult> func);
}
