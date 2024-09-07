using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Percolator.Crypto;
using DoubleRatchetModel = Percolator.Crypto.Grpc.DoubleRatchetModel;

namespace Percolator.Desktop.Main;

public class DoubleRatchetModelFactory
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ISerializer _serializer;
    private static readonly byte[] _appRootKeyBytes = [3,5,7,9,11,13,15,17,19,21,23,25,27,29,31,33];
    public DoubleRatchetModelFactory(ILoggerFactory loggerFactory, ISerializer serializer)
    {
        _loggerFactory = loggerFactory;
        _serializer = serializer;
    }
    public DoubleRatchetModel Create(
        byte[] rootKey,
        ECDiffieHellman ephemeral)
    {
        var doubleRatchetModel = DoubleRatchetModel.CreateReceiver(
            ephemeral,
            rootKey,
            _loggerFactory.CreateLogger<Percolator.Crypto.DoubleRatchetModel>(),
            _serializer);
        return doubleRatchetModel;
    }

    public byte[] CreateRootKey(
        ECDiffieHellmanPublicKey otherPublicKey,
        byte[] otherIdentity,
        ECDiffieHellman selfEphemeral)
    {
        var sharedDh = selfEphemeral.DeriveKeyMaterial(otherPublicKey);
        
        //todo: consider avoiding HDKF - just use sharedDh - it may not improve security
        return HKDF.Extract(HashAlgorithmName.SHA256,
            sharedDh,
            otherIdentity.Concat(_appRootKeyBytes).ToArray());
    }
}