using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Identity.Model;
using Percolator.Infrastructure.Network.Certificates;
using Percolator.Infrastructure.Network.Grpc;
using System.Security.Authentication;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Percolator.Identity;

namespace Percolator.Infrastructure.Network;

public class GrpcServerManager : IGrpcServerManager
{
    private readonly ILogger<GrpcServerManager> _logger;
    private readonly ITransportCertificateProvider _certificateProvider;
    private readonly IServiceProvider _primaryProvider;
    private WebApplication? _webApp;

    public GrpcServerManager(
        ILogger<GrpcServerManager> logger,
        ITransportCertificateProvider certificateProvider,
        IServiceProvider primaryProvider)
    {
        _logger = logger;
        _certificateProvider = certificateProvider;
        _primaryProvider = primaryProvider;
    }

    public async Task<ServerStartResult> StartAsync(SelfId selfId, ListeningPort port, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Starting gRPC server on port {Port} for identity {IdentityId}", port, selfId);

            // Get valid certificate (handles rotation if needed)
            var certificate = await _certificateProvider.GetValidCertificateAsync(selfId, ct);

            // Build secondary WebApplication for gRPC listener
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Production",
                ContentRootPath = Directory.GetCurrentDirectory()
            });

            // Configure Kestrel
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(port.Value, listenOptions =>
                {
                    listenOptions.UseHttps(httpsOptions =>
                    {
                        httpsOptions.ServerCertificate = certificate;
                        httpsOptions.SslProtocols = SslProtocols.Tls13;
                        httpsOptions.ClientCertificateMode = ClientCertificateMode.NoCertificate;
                    });
                });
            });

            // Register gRPC service activator that bridges to primary container
            builder.Services.AddSingleton<IGrpcServiceActivator<PercolatorMessageService>>(
                new PrimaryContainerServiceActivator<PercolatorMessageService>(_primaryProvider)
            );
            
            // Bridge the interceptor resolution to the primary container
            builder.Services.AddTransient<IdentityReadinessInterceptor>(sp => 
                _primaryProvider.GetRequiredService<IdentityReadinessInterceptor>());
            
            // Register gRPC services
            builder.Services.AddGrpc().AddServiceOptions<PercolatorMessageService>(options =>
            {
                options.Interceptors.Add<IdentityReadinessInterceptor>();
            });

            // Build the host
            var app = builder.Build();
            app.MapGrpcService<PercolatorMessageService>();

            // Start the host
            await app.StartAsync(ct);

            _webApp = app;
            _logger.LogInformation("gRPC server started successfully on port {Port}", port);

            return ServerStartResult.Succeeded();
        }
        catch (IOException ex) when (ex.Message.Contains("address already in use") || ex.Message.Contains("port"))
        {
            _logger.LogError(ex, "Port conflict detected");
            return ServerStartResult.Failed(ex.Message, isPortConflict: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start gRPC server");
            return ServerStartResult.Failed(ex.Message);
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_webApp != null)
        {
            _logger.LogInformation("Stopping gRPC server");
            await _webApp.StopAsync(ct);
            await _webApp.DisposeAsync();
            _webApp = null;
            _logger.LogInformation("gRPC server stopped");
        }
    }

    public async Task RestartAsync(SelfId selfId, ListeningPort port, CancellationToken ct)
    {
        await StopAsync(ct);
        await StartAsync(selfId, port, ct);
    }
}
