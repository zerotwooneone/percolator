namespace Desktop.Wpf.Shared.Config
{
    public sealed class LoggingOptions
    {
        public string Level { get; set; } = "Information"; // Trace|Debug|Information|Warning|Error|Critical|None
        public bool Console { get; set; } = true;
        public bool Debug { get; set; } = true;
        public FileLoggingOptions File { get; set; } = new();
    }

    public sealed class FileLoggingOptions
    {
        public bool Enabled { get; set; } = false;
        public string Path { get; set; } = "logs/desktop-wpf.log";
    }
}
