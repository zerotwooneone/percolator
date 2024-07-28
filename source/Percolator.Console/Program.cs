using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Percolator.Console;

/*const string KeyContainerName = "Percolator";
var csp = new CspParameters
{
    KeyContainerName = KeyContainerName,
    Flags = 
        CspProviderFlags.UseArchivableKey | 
        CspProviderFlags.UseMachineKeyStore | 
        CspProviderFlags.UseDefaultKeyContainer
};
using (var deleteRsa = new RSACryptoServiceProvider(csp))
{
    deleteRsa.PersistKeyInCsp = false;
    //this deletes the stored key, and disposes the object
    deleteRsa.Clear();

    Console.WriteLine("Deleted key");
}
return;*/

using var epheremalRsa = new RSACryptoServiceProvider()
{
    PersistKeyInCsp = false,
};

using var channel = GrpcChannel.ForAddress("http://localhost:5076");
var client = new Greeter.GreeterClient(channel);

var success = false;
do
{
    var payload = new HelloRequest.Types.Payload()
    {
        PublicKey = ByteString.CopyFrom(epheremalRsa.ExportRSAPublicKey()),
        TimeStampUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };
    var payloadBytes = payload.ToByteArray();
    var payloadHash = SHA256.Create().ComputeHash(payloadBytes);
    var payloadHashSignature = epheremalRsa.SignHash(payloadHash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var reply = await client.SayHelloAsync(
        new HelloRequest
        {
            Payload = payload,
            PayloadSignature = ByteString.CopyFrom(payloadHashSignature)
        });
    success = reply.ResponseTypeCase switch
    {
        HelloReply.ResponseTypeOneofCase.Proceed => true,
        HelloReply.ResponseTypeOneofCase.Empty => true, 
        _ => false
    };
    if (reply.ResponseTypeCase == HelloReply.ResponseTypeOneofCase.Busy)
    {
        var wakeUpTime = DateTimeOffset.FromUnixTimeMilliseconds(reply.Busy.NotBeforeUnixMs);
        var now = DateTimeOffset.UtcNow;
        if (wakeUpTime > now)
        {
            var timeToWait = wakeUpTime - now;
            Thread.Sleep(timeToWait);
        }
    }
    Console.WriteLine("Response Type: " + reply.ResponseTypeCase);
} while (!success);


//var h = new Handler();
//h.Handle(client).Wait();

Console.WriteLine("Press any key to exit...");

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

