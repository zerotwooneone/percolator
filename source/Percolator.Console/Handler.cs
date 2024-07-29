using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Percolator.Protobuf;

public class Handler
{
    public async Task Handle(Greeter.GreeterClient client)
    {
        await using var writeFile = File.OpenWrite($"{DateTimeOffset.Now.ToUnixTimeMilliseconds()}.bmp");
        var stream = client.Gimme(new Empty(), new CallOptions());

        while (await stream.ResponseStream.MoveNext())
        {
            var streamMessage = stream.ResponseStream.Current;
            //Console.WriteLine(streamMessage);
            var bytes = streamMessage.Bytes.SelectMany(b=>b.ToByteArray()).ToArray();
            writeFile.Position = streamMessage.StartIndex;
            writeFile.Write(bytes, 0, bytes.Length);
        }
        await writeFile.FlushAsync();
    }
}