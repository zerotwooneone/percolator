using Microsoft.Extensions.DependencyInjection;
using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.Runtime;
using Percolator.Apps.FileTransfer.Features.Storage;
using Percolator.Apps.FileTransfer.Features.SwarmCoordination;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.PluginSdk;

namespace Percolator.Apps.FileTransfer.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFileTransferApp(this IServiceCollection collection, bool enableIngressByDefault = false)
    {
        collection.AddSingleton<IAppPlugin, FileTransferPlugin>();
        collection.AddSingleton<FileTransferIngressFilter>(sp =>
            new FileTransferIngressFilter(sp.GetRequiredService<IFileTransferFilterPolicyRepository>())
            {
                IsEnabled = enableIngressByDefault
            });
        collection.AddSingleton<IAppIngressFilter>(sp => sp.GetRequiredService<FileTransferIngressFilter>());
        collection.AddSingleton<TransferSessionRuntimeOptions>(_ => new TransferSessionRuntimeOptions());
        collection.AddSingleton<IBackgroundPayloadDispatcher, ScopedBackgroundPayloadDispatcher>();
        collection.AddSingleton<ITransferSessionRuntime, TransferSessionRuntime>();
        collection.AddScoped<IAppPayloadHandler, FileTransferPayloadHandler>();
        collection.AddScoped<IFileTransferActionHandler, FileTransferActionHandler>();
        collection.AddScoped<ITransferOfferReaperService, TransferOfferReaperService>();

        return collection;
    }
}
