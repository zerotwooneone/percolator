namespace Percolator.Infrastructure.Vault;

using System.Data.Common;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Percolator.Domain.Security.Delegates;
using Percolator.Domain.Security.Ports;

public sealed class MasterKeyHandle : IMasterKeyHandle
{
    private IntPtr _ptr;
    private bool _disposed;

    public unsafe MasterKeyHandle(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("Database master key must be exactly 32 bytes.", nameof(key));
        }

        _ptr = Marshal.AllocHGlobal(32);
        key.CopyTo(new Span<byte>((void*)_ptr, 32));
    }

    public static MasterKeyHandle GenerateRandom()
    {
        Span<byte> randomKey = stackalloc byte[32];
        try
        {
            RandomNumberGenerator.Fill(randomKey);
            return new MasterKeyHandle(randomKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(randomKey);
        }
    }

    public unsafe void ApplyKeyToConnection(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (connection is SqliteConnection sqliteConn)
        {
            var handle = sqliteConn.Handle;
            if (handle == null)
            {
                throw new InvalidOperationException("SQLite connection handle is null. Connection must be open.");
            }

            var keySpan = new ReadOnlySpan<byte>((void*)_ptr, 32);
            int rc = SQLitePCL.raw.sqlite3_key(handle, keySpan);
            if (rc != SQLitePCL.raw.SQLITE_OK)
            {
                throw new InvalidOperationException($"sqlite3_key failed with result code {rc}.");
            }
        }
        else
        {
            throw new ArgumentException("Connection must be a Microsoft.Data.Sqlite.SqliteConnection.", nameof(connection));
        }
    }

    public unsafe void WithKeySpan(Action<ReadOnlySpan<byte>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(_disposed, this);

        action(new ReadOnlySpan<byte>((void*)_ptr, 32));
    }

    public unsafe TResult WithKeySpan<TResult>(ReadOnlySpanFunc<byte, TResult> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        ObjectDisposedException.ThrowIf(_disposed, this);

        return func(new ReadOnlySpan<byte>((void*)_ptr, 32));
    }

    public unsafe void Zeroize()
    {
        if (_disposed) return;

        if (_ptr != IntPtr.Zero)
        {
            CryptographicOperations.ZeroMemory(new Span<byte>((void*)_ptr, 32));
            Marshal.FreeHGlobal(_ptr);
            _ptr = IntPtr.Zero;
        }

        _disposed = true;
    }

    public void Dispose() => Zeroize();
}
