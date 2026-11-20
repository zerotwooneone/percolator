using System;
using System.IO;
using System.Security.Cryptography;

namespace Percolator.Simulator.Features.SwarmSimulation;

internal sealed record GeneratedFilePreset(
    string FilePath,
    string RelativeFileName,
    long TotalBytes,
    string Sha256Hex);

internal sealed class SyntheticFilePresetGenerator
{
    public const string Preset2Mb = "2 MB";
    public const string Preset25Mb = "25 MB";
    public const string Preset100Mb = "100 MB";

    public GeneratedFilePreset GeneratePreset(string targetDirectory, string presetSize = Preset2Mb)
    {
        ArgumentNullException.ThrowIfNull(targetDirectory);
        Directory.CreateDirectory(targetDirectory);

        long targetBytes = presetSize switch
        {
            Preset25Mb => 25L * 1024 * 1024,
            Preset100Mb => 100L * 1024 * 1024,
            _ => 2L * 1024 * 1024
        };

        string safeSizeName = presetSize.Replace(" ", "").ToLowerInvariant();
        string fileName = $"synthetic_dataset_{safeSizeName}.bin";
        string filePath = Path.Combine(targetDirectory, fileName);

        // If file already exists and has correct length, compute its SHA256 and return
        if (File.Exists(filePath) && new FileInfo(filePath).Length == targetBytes)
        {
            using var existingStream = File.OpenRead(filePath);
            byte[] existingHash = SHA256.HashData(existingStream);
            return new GeneratedFilePreset(filePath, fileName, targetBytes, Convert.ToHexString(existingHash).ToLowerInvariant());
        }

        // Generate deterministic pseudo-random bytes using XorShift64
        ulong state = 0x853c49e6748fea9bUL ^ (ulong)targetBytes;
        byte[] buffer = new byte[64 * 1024];

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length))
        {
            long written = 0;
            while (written < targetBytes)
            {
                int toWrite = (int)Math.Min(buffer.Length, targetBytes - written);
                for (int i = 0; i < toWrite; i += 8)
                {
                    state ^= state << 13;
                    state ^= state >> 7;
                    state ^= state << 17;

                    int spanLen = Math.Min(8, toWrite - i);
                    Span<byte> target = buffer.AsSpan(i, spanLen);
                    BitConverter.TryWriteBytes(target, state);
                }

                fileStream.Write(buffer, 0, toWrite);
                sha256.AppendData(buffer, 0, toWrite);
                written += toWrite;
            }
            fileStream.Flush();
        }

        byte[] finalHash = sha256.GetHashAndReset();
        return new GeneratedFilePreset(filePath, fileName, targetBytes, Convert.ToHexString(finalHash).ToLowerInvariant());
    }
}
