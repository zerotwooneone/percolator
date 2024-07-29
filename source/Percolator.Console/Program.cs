using System.Security.Cryptography;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Percolator.Protobuf;
using Percolator.Protobuf.Stream;
using StreamMessage = Percolator.Protobuf.Stream.StreamMessage;

var appCancellationTokenSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => appCancellationTokenSource.Cancel();

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

using var identityRsa = new RSACryptoServiceProvider()
{
    PersistKeyInCsp = false,
};

using var ephemeralRsa = new RSACryptoServiceProvider()
{
    PersistKeyInCsp = false,
};

using var channel = GrpcChannel.ForAddress("http://localhost:5076");
var client = new Greeter.GreeterClient(channel);

var success = false;
HelloReply reply;
do
{
    var payload = new HelloRequest.Types.Payload()
    {
        IdentityKey = ByteString.CopyFrom(identityRsa.ExportRSAPublicKey()),
        EphemeralKey = ByteString.CopyFrom(ephemeralRsa.ExportRSAPublicKey()),
        TimeStampUnixUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    };
    var payloadBytes = payload.ToByteArray();
    var payloadHashSignature = identityRsa.SignData(payloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    reply = await client.SayHelloAsync(
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
} while (!success && !appCancellationTokenSource.Token.IsCancellationRequested);

if (reply.ResponseTypeCase != HelloReply.ResponseTypeOneofCase.Proceed || appCancellationTokenSource.IsCancellationRequested)
{
    return;
}

using Aes aes = new AesCryptoServiceProvider();
aes.IV = reply.Proceed.Payload.Iv.ToByteArray();

// Decrypt the session key
RSAOAEPKeyExchangeDeformatter keyDeformatter = new RSAOAEPKeyExchangeDeformatter(ephemeralRsa);
aes.Key = keyDeformatter.DecryptKeyExchange(reply.Proceed.Payload.EncryptedSessionKey.ToByteArray());

using MemoryStream plaintext = new MemoryStream();
await using CryptoStream cs = new CryptoStream(plaintext, aes.CreateDecryptor(), CryptoStreamMode.Write);

// cs.Write(encryptedMessage, 0, encryptedMessage.Length);
// cs.Close();
//
// string message = Encoding.UTF8.GetString(plaintext.ToArray());
// Console.WriteLine(message);

// var serverEphemeralRsa = new RSACryptoServiceProvider()
// {
//     PersistKeyInCsp = false
// };
// serverEphemeralRsa.ImportRSAPublicKey(reply.Proceed.Payload.EphemeralKey.ToByteArray(), out _);

var serverIdentityRsa = new RSACryptoServiceProvider()
{
    PersistKeyInCsp = false
};
serverIdentityRsa.ImportRSAPublicKey(reply.Proceed.Payload.IdentityKey.ToByteArray(), out _);

var responsePayloadBytes = reply.Proceed.Payload.ToByteArray();
if (!serverIdentityRsa.VerifyData(responsePayloadBytes, reply.Proceed.PayloadSignature.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
{
    Console.WriteLine("Failed to verify signature");
    return;
}
Console.WriteLine("Success! The response payload matches the signature");

var streamClient = new Streamer.StreamerClient(channel);
var stream = streamClient.Begin(cancellationToken: appCancellationTokenSource.Token);

var payloadMessage = new StreamMessage.Types.Payload
{
    Ping = new StreamMessage.Types.Payload.Types.PingMessage
    {
        TimeStampUnixUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    }
};
var pingBytes = payloadMessage.ToByteArray();
var encrypedPingBytes = await aes.NaiveEncrypt(pingBytes);
var pingWrapper = new StreamMessage
{
    Identity = ByteString.CopyFrom(identityRsa.ExportRSAPublicKey()),
    EncryptedPayload = ByteString.CopyFrom(encrypedPingBytes)
};

var readTask = new TaskFactory().StartNew(() =>
{
    while (!appCancellationTokenSource.IsCancellationRequested && stream.ResponseStream.MoveNext().Result)
    {
        if (appCancellationTokenSource.IsCancellationRequested)
        {
            break;
        }
        var streamMessage = stream.ResponseStream.Current;
        var decryptedBytes = aes.NaiveDecrypt(streamMessage.EncryptedPayload.ToByteArray()).Result;
        var responsePayload = StreamMessage.Types.Payload.Parser.ParseFrom(decryptedBytes);
        if (responsePayload.PayloadTypeCase == StreamMessage.Types.Payload.PayloadTypeOneofCase.None)
        {
            Console.WriteLine("got empty payload");
            continue;
        }
        switch (responsePayload.PayloadTypeCase)
        {
            case StreamMessage.Types.Payload.PayloadTypeOneofCase.Pong:
                Console.WriteLine($"got pong: {responsePayload.Pong.Delta}");
                continue;
            case StreamMessage.Types.Payload.PayloadTypeOneofCase.UserChat:
                Console.WriteLine($"got user chat: {responsePayload.UserChat.Message}");
                continue;
        }
    }
}, appCancellationTokenSource.Token); 


var pingTask = stream.RequestStream.WriteAsync(pingWrapper)
    .ContinueWith(_=>
    {
        Thread.Sleep(300);
        stream.RequestStream.WriteAsync(new StreamMessage
        {
            Identity = ByteString.CopyFrom(identityRsa.ExportRSAPublicKey()),
            EncryptedPayload = ByteString.CopyFrom(aes.NaiveEncrypt(new StreamMessage.Types.Payload
            {
                UserChat = new StreamMessage.Types.Payload.Types.UserMessage
                {
                    TimeStampUnixUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Message = "hello"
                }
            }.ToByteArray()).Result)
        }).Wait();
    })
    ;

await Task.WhenAll(readTask,pingTask);

stream.Dispose();

//var h = new Handler();
//h.Handle(client).Wait();

Console.WriteLine("Press any key to exit...");