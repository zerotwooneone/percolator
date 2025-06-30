using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Percolator.Node
{
    public class MessageListenerService : IHostedService
    {
        private readonly ILogger<MessageListenerService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private IWebHost? _host;

        public MessageListenerService(ILogger<MessageListenerService> logger, IServiceProvider serviceProvider)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting MessageListenerService...");

            // Build a separate host for the gRPC server to listen for incoming messages
            _host = new WebHostBuilder()
                .UseKestrel(options =>
                {
                    // Configure Kestrel to listen on the same port as the main application
                    // This assumes the main application is also running Kestrel and sharing the port.
                    // In a real-world scenario, you might have a single Kestrel instance configured
                    // to host multiple gRPC services.
                    options.ListenAnyIP(5001, listenOptions =>
                    {
                        listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2;
                    });
                })
                .ConfigureServices(services =>
                {
                    // Use the same service provider to ensure shared singletons like DirectSessionManager
                    services.AddSingleton(_serviceProvider.GetRequiredService<Percolator.Application.Network.PercolatorMessageService>());
                    services.AddGrpc();
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGrpcService<Percolator.Application.Network.PercolatorMessageService>();
                    });
                })
                .Build();

            // Start the host in the background
            _ = _host.StartAsync(cancellationToken);

            _logger.LogInformation("MessageListenerService started.");
            return Task.CompletedTask;
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