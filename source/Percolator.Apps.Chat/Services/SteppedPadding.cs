namespace Percolator.Apps.Chat.Services;

public static class SteppedPadding
{
    public static int CalculatePaddedLength(int unpaddedLength)
    {
        if (unpaddedLength <= 256) return 256;
        if (unpaddedLength <= 512) return 512;
        if (unpaddedLength <= 1024) return 1024;
        if (unpaddedLength <= 2048) return 2048;
        if (unpaddedLength <= 4096) return 4096;
        return ((unpaddedLength + 4095) / 4096) * 4096;
    }
}
