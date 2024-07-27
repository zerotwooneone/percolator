// See https://aka.ms/new-console-template for more information

using System.Security.Cryptography;
using System.Text;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Percolator.Console;

const string KeyContainerName = "Percolator";

var csp = new CspParameters
{
    KeyContainerName = KeyContainerName,
    Flags = 
        CspProviderFlags.UseArchivableKey | 
        CspProviderFlags.UseMachineKeyStore | 
        CspProviderFlags.UseDefaultKeyContainer
};

byte[]? encrypted = null;
//this gets or creates the key
using (var rsa = new RSACryptoServiceProvider(csp))
{
    //Console.WriteLine($"rsa key: {rsa.ToXmlString(true)}");
    encrypted = rsa.Encrypt("Hello World!"u8.ToArray(), false);
}

// using (var deleteRsa = new RSACryptoServiceProvider(csp))
// {
//     deleteRsa.PersistKeyInCsp = false;
//     //this deletes the stored key, and disposes the object
//     deleteRsa.Clear();
//
//     Console.WriteLine("Deleted key");
// }

using (var decrypter = new RSACryptoServiceProvider(csp))
{
    var decrypted = decrypter.Decrypt(encrypted, false);
    Console.WriteLine(Encoding.UTF8.GetString(decrypted, 0, decrypted.Length));
}

using var channel = GrpcChannel.ForAddress("http://localhost:5076");
var client = new Greeter.GreeterClient(channel);
var reply = await client.SayHelloAsync(
    new HelloRequest { Name = "GreeterClient" });
Console.WriteLine("Greeting: " + reply.Message);

var h = new Handler();
h.Handle(client).Wait();

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

