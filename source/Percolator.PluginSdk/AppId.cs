namespace Percolator.PluginSdk;

public readonly record struct AppId(byte Value)
{
    public static readonly AppId SystemControl = new(0x00);
    public static readonly AppId Chat = new(0x01);
    public static readonly AppId Discovery = new(0x02);
    public static readonly AppId FileTransferControl = new(0x03);

    public override string ToString() => $"AppId(0x{Value:X2})";
}
