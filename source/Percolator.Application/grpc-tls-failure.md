# gRPC TLS Handshake Failure Analysis

This document records our attempts to resolve persistent TLS handshake failures causing `ObjectDisposedException` on `SslStream` during Trust-On-First-Use (TOFU) gRPC connections in the Percolator application.

## Problem Description

When attempting to establish a secure gRPC connection using the Trust-On-First-Use (TOFU) model, we experience the following issues:

1. Initial TLS handshake succeeds (we can extract the certificate)
2. Subsequent gRPC connection fails with either:
   - `ObjectDisposedException` on `SslStream` during HTTP/2 handshake
   - `Grpc.Core.RpcException` with "failed to connect to all addresses" and "Failed to pick subchannel"

## Architecture Requirements

- Strict domain isolation between `Percolator.Identity`, `Percolator.Network`, etc.
- TOFU security model where initial connections trust the first certificate seen from a peer
- Proper certificate lifecycle management in .NET 9.0 (avoiding obsolete constructors)
- Robust error handling and logging

## Attempted Solutions

### Attempt 1: Direct Grpc.Net.Client with HttpClientHandler

```csharp
var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) => 
    {
        // Capture and validate certificate
        return true;
    }
};

var httpClient = new HttpClient(handler);
var channel = GrpcChannel.ForAddress($"https://{endpoint.Host}:{endpoint.Port}", new GrpcChannelOptions
{
    HttpClient = httpClient
});
```

**Failures:**
- `ObjectDisposedException`: SslStream was disposed before HTTP/2 handshake completed
- Multiple certificate validation callbacks caused conflicts
- Certificate lifecycle issues (certificates being disposed too early)

### Attempt 2: Force HTTP/1.1 Instead of HTTP/2

```csharp
var handler = new SocketsHttpHandler
{
    // Use HTTP/1.1 to avoid HTTP/2 handshake issues
    EnableMultipleHttp2Connections = false,
    // ... other settings
};

httpClient.DefaultRequestVersion = HttpVersion.Version11;
httpClient.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
```

**Failures:**
- gRPC requires HTTP/2, so forcing HTTP/1.1 caused protocol negotiation failures
- `System.NotSupportedException`: HTTP/1.1 is not supported for gRPC
- Attempted to modify `SocketsHttpHandler` but APIs were limited

### Attempt 3: Separate TLS Handshake from gRPC Connection

```csharp
// First do direct TLS handshake to capture certificate
var capturedCert = await _tlsHandshakeService.CaptureCertificateAsync(endpoint);

// Then establish gRPC channel with insecure credentials but using the validated certificate
var channel = new Channel(
    $"{endpoint.Host}:{endpoint.Port}",
    ChannelCredentials.Insecure,
    // Channel options...
);
```

**Failures:**
- Server refused insecure connections (as expected for security)
- "failed to connect to all addresses" error indicating incompatible protocol
- Insecure channel doesn't match the security requirements of the server

### Attempt 4: Custom Grpc.Core SslCredentials with PEM

```csharp
var sslCredentials = new SslCredentials(
    // No root certificates - we'll validate in the callback
    rootCertificates: null,
    keyCertificatePairs: null,
    verifyPeerCallback: (context) => {
        // Custom validation logic
        return true;
    }
);
```

**Failures:**
- Build errors with incorrect `SslCredentials` constructor usage
- Issues with callback signature not matching expected delegate
- Grpc.Core.RpcException: "failed to connect to all addresses"

### Attempt 5: Grpc.Net.Client with HttpClientHandler and SNI Support

```csharp
var handler = new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
    {
        // Compare with our expected certificate
        bool isMatch = cert.Thumbprint.Equals(certCopy.Thumbprint, StringComparison.OrdinalIgnoreCase);
        return isMatch;
    }
};

var httpClient = new HttpClient(handler);
var uri = new Uri($"https://{endpoint.Host}:{endpoint.Port}");
var channel = GrpcChannel.ForAddress(uri, new GrpcChannelOptions { HttpClient = httpClient });
```

**Failures:**
- Same "failed to connect to all addresses" error
- HTTP/2 connection failed to establish despite correct TLS certificate validation

## Root Causes Identified

1. **Certificate Lifecycle**: .NET 9.0 has stricter certificate lifecycle management. Using obsolete `X509Certificate2` constructors caused warnings and potential disposal issues.

2. **HTTP/2 Protocol Requirements**: gRPC requires HTTP/2, but HTTP/2 has stricter requirements for TLS handshakes.

3. **Server Configuration Mismatch**: The server's gRPC configuration may not be compatible with how we're attempting to connect.

4. **Dual TLS Validation**: When we attempt to separate certificate validation from gRPC, we end up with two competing validation mechanisms.

5. **Networking Issues**: "Failed to connect to all addresses" suggests network-level connectivity issues rather than just TLS.

## Configuration Details Examined

1. **Server Configuration**: The server uses standard `MapGrpcService<PercolatorMessageService>()` for gRPC hosting.

2. **Client-side Approaches**:
   - `Grpc.Core.Channel` with various `ChannelCredentials`
   - `Grpc.Net.Client.GrpcChannel` with custom `HttpClient`
   - Direct TLS handshake with `SslStream`

3. **Protocol Requirements**:
   - gRPC requires HTTP/2
   - HTTP/2 has strict TLS requirements
   - Mixed protocol handling is problematic

## Next Steps and Recommendations

1. **Consider Unified Handshake**: Instead of separating TLS handshake from gRPC, ensure they're unified and consistent.

2. **Server-side Verification**: Examine the server's endpoint configuration, especially TLS and HTTP/2 settings.

3. **Network Diagnostics**: Run network diagnostics to confirm basic connectivity before TLS handshake.

4. **Consider gRPC-Web**: As an alternative, gRPC-Web works over HTTP/1.1 and might avoid HTTP/2 handshake issues.

5. **Certificate Store Approach**: Consider using the system certificate store for validation rather than in-memory comparison.

6. **Alternative Authentication**: Explore moving authentication to the application layer with tokens rather than relying entirely on TLS.

## Key Lessons Learned

1. gRPC's HTTP/2 requirement creates complex TLS handshake dependencies
2. Separating the TLS handshake from channel establishment creates disposal and lifecycle issues
3. Certificate validation callbacks need to be very carefully managed to avoid lifecycle issues
4. The "failed to connect to all addresses" error indicates a fundamental connectivity issue, not just a TLS problem
5. .NET 9.0's certificate lifecycle management is more strict and requires modern APIs like `X509CertificateLoader`