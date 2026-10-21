using Microsoft.Extensions.DependencyInjection;
using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Apps.FileTransfer.Features.MultiStreamEngine;
using Percolator.Apps.FileTransfer.Features.Storage;
using Percolator.Apps.FileTransfer.Features.SwarmCoordination;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.PluginSdk;

namespace Percolator.Apps.FileTransfer.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddFileTransferApp(this IServiceCollection collection)
    {
        collection.AddSingleton<IAppPlugin, FileTransferPlugin>();
        collection.AddSingleton<IAppIngressFilter, FileTransferIngressFilter>();
        collection.AddSingleton<IAppPayloadHandler, FileTransferPayloadHandler>();
        collection.AddScoped<IFileTransferActionHandler, FileTransferActionHandler>();
        collection.AddScoped<IFileTransferQueryService, FileTransferQueryService>();
        collection.AddScoped<TransferSessionCoordinator>();
        collection.AddScoped<DiskChunkAssembler>();
        collection.AddScoped<ChunkPipelineScheduler>();
        collection.AddScoped<SwarmCoordinator>();
        collection.AddScoped<EndgameCoordinator>();
        collection.AddScoped<ITransferOfferReaperService, TransferOfferReaperService>();

        return collection;
    }
}
