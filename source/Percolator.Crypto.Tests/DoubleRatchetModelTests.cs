using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Moq;

namespace Percolator.Crypto.Tests;

public class DoubleRatchetModelTests
{
    private ISerializer _serializer;
    private Mock<ILogger<DoubleRatchetModel>> _logger;

    [SetUp]
    public void Setup()
    {
        _serializer = new GrpcSerializer.GrpcSerializer();
        _logger = new Mock<ILogger<DoubleRatchetModel>>();
    }

    [Test]
    public void EncryptDecrypt()
    {
        var bobIdentity = ECDiffieHellman.Create();
        //var aliceIdentity = ECDiffieHellman.Create();
        byte[] secretKey = [3, 5, 9, 13];
        var alice = DoubleRatchetModel.CreateSender(bobIdentity.PublicKey, secretKey,
            _logger.Object, _serializer);

        byte[] associatedData = [9, 9, 9, 9, 9];
        var plainText1 = "hello, attack at dawn"u8.ToArray();
        var encrypted = alice.RatchetEncrypt(plainText1, associatedData);

        var bob = DoubleRatchetModel.CreateReceiver(bobIdentity, secretKey,
            _logger.Object, _serializer);

        var decrypted1 = bob.RatchetDecrypt(encrypted.header, encrypted.encrypted, encrypted.headerSignature);
        CollectionAssert.AreEqual(plainText1,decrypted1);

        var plainText2 = "sir, this is an arby's"u8.ToArray();
        var bobEnc = bob.RatchetEncrypt(plainText2, associatedData);
        var decrypted2 = alice.RatchetDecrypt(bobEnc.header, bobEnc.encrypted, bobEnc.headerSignature);
        
        CollectionAssert.AreEqual(plainText2,decrypted2);
    }
}