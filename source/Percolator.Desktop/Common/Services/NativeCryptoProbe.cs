// SPECIFICATION GOVERNANCE: Read Percolator.Desktop/spec/CodeBehind.spec.md before modifying this file.

using System;
using Signal.Interop;

namespace Percolator.Desktop.Common.Services;

/// <summary>
/// Probe for the native signal_shim cryptographic engine ([DEP-DESKTOP-PRE-ACTIVATION-STEALTH]).
/// Injectable so ViewModels can verify the native interop layer loads without calling vendor statics.
/// </summary>
public interface INativeCryptoProbe
{
    /// <summary>Returns the native interop diagnostic code; expected <c>42</c>.</summary>
    int TestConnection();
}

/// <inheritdoc />
public sealed class NativeCryptoProbe : INativeCryptoProbe
{
    /// <inheritdoc />
    public int TestConnection()
    {
        try
        {
            return SignalCrypto.TestConnection();
        }
        catch (Exception)
        {
            // Native library failed to load (missing VC++ runtime, missing .dll/.so).
            return -1;
        }
    }
}
