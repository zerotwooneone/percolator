using Google.Protobuf;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Percolator.Grpc;

namespace Percolator.Grpc.Services;

public class GreeterService : Greeter.GreeterBase
{
    private readonly ILogger<GreeterService> _logger;

    public GreeterService(ILogger<GreeterService> logger)
    {
        _logger = logger;
    }

    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
    {
        return Task.FromResult(new HelloReply
        {
            Message = "Hello " + request.Name
        });
    }

    public override Task Gimme(Empty request, IServerStreamWriter<StreamMessage> responseStream, ServerCallContext context)
    {
        using var readStream = File.OpenRead("gradient.bmp");
        const int bufferSize = 1024;
        var buffer = new byte[bufferSize];
        int startIndex = 0;
        while (readStream.Read(buffer, 0, buffer.Length) > 0)
        {
            var streamMessage = new StreamMessage
            {
                StartIndex = startIndex
            };
            streamMessage.Bytes.Add(ByteString.CopyFrom(buffer));
            responseStream.WriteAsync(streamMessage);
            startIndex += bufferSize;
        }
        return Task.CompletedTask;
    }
}