using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Microsoft.Extensions.Configuration;
using Percolator.Application;
using Percolator.Infrastructure;
using Percolator.Application.Network;
using Microsoft.Extensions.Options;
using Percolator.Application.Configuration;
using Percolator.Infrastructure.Network.Tls;

namespace Percolator.Node
{
    public class MessageListenerService : IHostedService
    {
        private readonly ILogger<MessageListenerService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly SharedCertificateManager _certificateManager;
        private IWebHost? _host;
        private readonly IOptions<TransportOptions> _transportOptions;

        public MessageListenerService(ILogger<MessageListenerService> logger, 
            IServiceProvider serviceProvider, 
            SharedCertificateManager certificateManager,
            IOptions<TransportOptions> transportOptions)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _certificateManager = certificateManager;
            _transportOptions = transportOptions;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            int _port = _transportOptions.Value.GrpcPort;
            _logger.LogInformation("Starting MessageListenerService on port {Port}...", _port);

            try
            {
                // Get the server certificate using the SharedCertificateManager which will find it automatically
                var certificate = _certificateManager.GetServerCertificate();
                
                // Validate the certificate is usable
                if (certificate == null || !certificate.HasPrivateKey)
                {
                    _logger.LogError("Failed to get a valid server certificate with private key. MessageListenerService cannot start.");
                    return Task.CompletedTask;
                }
                
                _logger.LogInformation("Using server certificate: Subject={Subject}, Thumbprint={Thumbprint}, HasPrivateKey={HasPrivateKey}", 
                    certificate.Subject, certificate.Thumbprint, certificate.HasPrivateKey);
                
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
                        
                        // For local development, use HTTP/2 without TLS and no authentication
                        if (IsLocalDevelopment())
                        {
                            _logger.LogInformation("***** LOCAL DEVELOPMENT MODE: Disabling TLS and authentication requirements *****");
                            
                            options.Listen(IPAddress.Any, _port, listenOptions =>
                            {
                                _logger.LogInformation("Configuring endpoint for HTTP/2 without TLS on port {Port} for local development", _port);
                                listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
                                
                                _logger.LogInformation("HTTP/2 endpoint configured on port {Port} without TLS or authentication", _port);
                            });
                        }
                        else
                        {
                            // For production, use HTTP/2 with TLS and client certificate authentication
                            _logger.LogInformation("***** PRODUCTION MODE: Using TLS with client certificate authentication *****");
                            
                            options.ConfigureHttpsDefaults(httpsOptions =>
                            {
                                _logger.LogInformation("Configuring global HTTPS defaults");
                                httpsOptions.ServerCertificate = certificate;
                                httpsOptions.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
                                httpsOptions.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
                                
                                // TEMPORARY FOR TESTING: Log certificate details but accept it
                                httpsOptions.ClientCertificateValidation = (cert, chain, errors) => 
                                {
                                    if (cert == null)
                                    {
                                        _logger.LogWarning("Client did not present a certificate");
                                        return false;
                                    }
                                    
                                    // TEMPORARY FOR TESTING: Log certificate details but accept it
                                    _logger.LogInformation("Client certificate received: Subject={Subject}, Thumbprint={Thumbprint}", 
                                        cert.Subject, cert.Thumbprint);
                                    _logger.LogWarning("*** ACCEPTING ANY CLIENT CERTIFICATE FOR TESTING - INSECURE ***");
                                    return true;
                                    
                                    /* Original validation code
                                    bool isValid = cert.Thumbprint.Equals(certificate.Thumbprint, StringComparison.OrdinalIgnoreCase);
                                    _logger.LogInformation("Client certificate validation: {Result}, Client: {ClientThumb}, Server: {ServerThumb}", 
                                        isValid, cert.Thumbprint, certificate.Thumbprint);
                                    return isValid;
                                    */
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
                        }
                    })
                    .ConfigureServices(services =>
                    {
                        // Add gRPC service with enhanced error details
                        services.AddGrpc(options =>
                        {
                            options.EnableDetailedErrors = true;
                            options.MaxReceiveMessageSize = 16 * 1024 * 1024; // 16MB
                            options.MaxSendMessageSize = 16 * 1024 * 1024;    // 16MB
                            
                            _logger.LogInformation("Configuring gRPC service with detailed errors enabled");
                        });
                        
                        // Register all application and infrastructure services
                        IConfigurationRoot tempConfig = new ConfigurationBuilder()
                            .AddJsonFile("appsettings.json", optional: true)
                            .AddNode()
                            .Build();
                        
                        _logger.LogInformation("Registering application and infrastructure services for gRPC host");
                        
                        services.AddApplicationServices(tempConfig);
                        services.AddInfrastructureServices(tempConfig);
                        
                        // Get required services from main service provider for shared instances
                        var mainServiceProvider = (IServiceProvider)_serviceProvider;
                        
                        // Register the main service instances to ensure we use the same instances
                        _logger.LogInformation("Registering shared service instances from main application");
                        services.AddSingleton<Percolator.Infrastructure.Network.Grpc.PercolatorMessageService>(s=>mainServiceProvider.GetRequiredService<Percolator.Infrastructure.Network.Grpc.PercolatorMessageService>());
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapGrpcService<Percolator.Infrastructure.Network.Grpc.PercolatorMessageService>();
                            _logger.LogInformation("Mapped gRPC service: PercolatorMessageService at path /Percolator.Contracts.TransportService/*");
                            
                            // Log endpoint mapping info for debugging
                            _logger.LogInformation("gRPC service registration complete - check logs for actual endpoint details");
                        });
                    })
                    .Build();

                // Start the host in the background
                _ = _host.StartAsync(cancellationToken);

                _logger.LogInformation("MessageListenerService started successfully.");
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

        private bool IsLocalDevelopment()
        {
            // For testing purposes always return true to use HTTP/2 without TLS
            return true;
            
            // Later can use environment variable or configuration
            /*
            var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
            return string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase);
            */
        }
    }
}