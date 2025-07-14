using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System;
using System.Security.Cryptography.X509Certificates;
using Percolator.Application.Network;

namespace Percolator.Node
{
    public class MessageListenerService : IHostedService
    {
        private readonly ILogger<MessageListenerService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly SharedCertificateManager _certificateManager;
        private IWebHost? _host;
        private int _port;

        public MessageListenerService(ILogger<MessageListenerService> logger, 
            IServiceProvider serviceProvider, 
            SharedCertificateManager certificateManager,
            int port = 5001)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _certificateManager = certificateManager;
            _port = port;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting MessageListenerService on port {Port}...", _port);

            try
            {
                // Get the server certificate using the SharedCertificateManager which will find it automatically
                var certificate = _certificateManager.GetServerCertificate();
                
                // Validate the certificate is usable
                if (certificate == null || !certificate.HasPrivateKey)
                {
                    _logger.LogError("Server certificate is missing or doesn't have a private key");
                    throw new InvalidOperationException("Server certificate is missing or doesn't have a private key");
                }
                
                _logger.LogInformation("Using TLS certificate with thumbprint: {Thumbprint}, Subject: {Subject}, HasPrivateKey: {HasPrivateKey}",
                    certificate.Thumbprint, certificate.Subject, certificate.HasPrivateKey);
                
                // Build a separate host for the gRPC server to listen for incoming messages
                _host = new WebHostBuilder()
                    .UseKestrel(options =>
                    {
                        // Configure HTTP/2 specific limits
                        options.Limits.Http2.MaxStreamsPerConnection = 100;
                        // MaxConcurrentStreams not available in this .NET version
                        options.Limits.Http2.InitialConnectionWindowSize = 1024 * 1024; // 1MB
                        options.Limits.Http2.InitialStreamWindowSize = 1024 * 1024;     // 1MB
                        
                        _logger.LogInformation("Configured HTTP/2 limits: MaxStreamsPerConnection={MaxStreams}", 
                            options.Limits.Http2.MaxStreamsPerConnection);
                        
                        options.ConfigureHttpsDefaults(httpsOptions =>
                        {
                            _logger.LogInformation("Configuring global HTTPS defaults");
                            httpsOptions.ServerCertificate = certificate;
                            httpsOptions.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
                            httpsOptions.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
                            
                            // Simple certificate validation - just check if it matches our shared certificate
                            httpsOptions.ClientCertificateValidation = (cert, chain, errors) => 
                            {
                                if (cert == null)
                                {
                                    _logger.LogWarning("Client did not present a certificate");
                                    return false;
                                }
                                
                                bool isValid = cert.Thumbprint.Equals(certificate.Thumbprint, StringComparison.OrdinalIgnoreCase);
                                _logger.LogInformation("Client certificate validation: {Result}, Client: {ClientThumb}, Server: {ServerThumb}", 
                                    isValid, cert.Thumbprint, certificate.Thumbprint);
                                return isValid;
                            };
                            
                            // ALPN is handled automatically by Kestrel when HTTP/2 is enabled
                            _logger.LogInformation("TLS ALPN will be configured for HTTP/2 protocol negotiation");
                        });

                        // Configure endpoint for HTTP/2 only (not HTTP/1.1)
                        options.ListenAnyIP(_port, listenOptions =>
                        {
                            _logger.LogInformation("Configuring endpoint for HTTP/2 only on port {Port}", _port);
                            listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
                            
                            // UseHttps will use the defaults configured above
                            listenOptions.UseHttps();
                            
                            _logger.LogInformation("HTTPS endpoint configured on port {Port} with HTTP/2 protocol", _port);
                        });
                    })
                    .ConfigureServices(services =>
                    {
                        // Use the same service provider to ensure shared singletons
                        services.AddSingleton(_serviceProvider.GetRequiredService<Percolator.Application.Network.PercolatorMessageService>());
                        services.AddGrpc(options => 
                        {
                            // Configure gRPC options if needed
                            options.MaxReceiveMessageSize = 4 * 1024 * 1024; // 4 MB
                            options.MaxSendMessageSize = 4 * 1024 * 1024;    // 4 MB
                        });
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapGrpcService<Percolator.Application.Network.PercolatorMessageService>();
                            _logger.LogInformation("Mapped gRPC service: PercolatorMessageService");
                        });
                    })
                    .Build();

                // Start the host in the background
                _ = _host.StartAsync(cancellationToken);

                _logger.LogInformation("MessageListenerService started successfully with TLS enabled.");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start MessageListenerService: {ErrorMessage}", ex.Message);
                throw;
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Stopping MessageListenerService...");
            if (_host != null)
            {
                await _host.StopAsync(cancellationToken);
            }
            _logger.LogInformation("MessageListenerService stopped.");
        }
    }
}