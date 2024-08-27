using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Crypto.Grpc;

namespace Percolator.Crypto.Tests;

public class DoubleRatchetModelTests
{
    private ISerializer _serializer;
    private ILogger<DoubleRatchetModel> _logger;

    [SetUp]
    public void Setup()
    {
        _serializer = new GrpcSerializer();
        
        var serviceProvider = new ServiceCollection()
            .AddLogging()
            .BuildServiceProvider();

        _logger = serviceProvider.GetRequiredService<ILogger<DoubleRatchetModel>>();
    }

    [Test]
    public void SendAndReply()
    {
        var bobIdentity = ECDiffieHellman.Create();
        byte[] secretKey = [3, 5, 9, 13];
        var alice = DoubleRatchetModel.CreateSender(bobIdentity.PublicKey, secretKey,
            _logger, _serializer);

        byte[] associatedData = [9, 9, 9, 9, 9];
        var plainText1 = "hello, attack at dawn"u8.ToArray();
        var encrypted = alice.RatchetEncrypt(plainText1, associatedData);

        var bob = DoubleRatchetModel.CreateReceiver(bobIdentity, secretKey,
            _logger, _serializer);

        var tuple1 = bob.RatchetDecrypt(encrypted.header, encrypted.encrypted, encrypted.headerSignature);
        CollectionAssert.AreEqual(plainText1,tuple1.Value.plainText);

        var plainText2 = "sir, this is an arby's"u8.ToArray();
        var bobEnc = bob.RatchetEncrypt(plainText2, associatedData);
        var tuple2 = alice.RatchetDecrypt(bobEnc.header, bobEnc.encrypted, bobEnc.headerSignature);
        
        CollectionAssert.AreEqual(plainText2,tuple2.Value.plainText);
    }
    
    [Test]
    public void DelayedSendAndReply()
    {
        var (alice, bob) = CreateHandshake();

        byte[] associatedData = [9, 9, 9, 9, 9];
        var delayedBytes = "this is the delayed message"u8.ToArray();
        var delayed = alice.RatchetEncrypt(delayedBytes, associatedData);
        var firstBytes = "this is the first sent message"u8.ToArray();
        var first = alice.RatchetEncrypt(firstBytes, associatedData);
        
        var tuple = bob.RatchetDecrypt(first.header, first.encrypted, first.headerSignature);
        CollectionAssert.AreEqual(firstBytes,tuple.Value.plainText);
        var tupleDelayed = bob.RatchetDecrypt(delayed.header, delayed.encrypted, delayed.headerSignature);
        CollectionAssert.AreEqual(delayedBytes,tupleDelayed.Value.plainText);
    }

    private (DoubleRatchetModel alice, DoubleRatchetModel bob) CreateHandshake()
    {
        var bobIdentity = ECDiffieHellman.Create();
        byte[] secretKey = [3, 5, 9, 13];
        var alice = DoubleRatchetModel.CreateSender(bobIdentity.PublicKey, secretKey,
            _logger, _serializer);

        byte[] associatedData = [9, 9, 9, 9, 9];
        var plainText1 = "hello, attack at dawn"u8.ToArray();
        var encrypted1 = alice.RatchetEncrypt(plainText1, associatedData);

        var bob = DoubleRatchetModel.CreateReceiver(bobIdentity, secretKey,
            _logger, _serializer);

        bob.RatchetDecrypt(encrypted1.header, encrypted1.encrypted, encrypted1.headerSignature);
        return (alice, bob);
    }
}