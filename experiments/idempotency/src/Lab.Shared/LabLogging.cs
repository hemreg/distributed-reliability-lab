using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Lab.Shared;

public static class LabLogging
{
    /// <summary>One compact JSON object per line; framework noise above warning only.</summary>
    public static ILoggingBuilder AddLabJsonConsole(this ILoggingBuilder logging)
    {
        logging.ClearProviders();
        logging.AddConsole(o => o.FormatterName = CompactJsonConsoleFormatter.FormatterName);
        logging.AddConsoleFormatter<CompactJsonConsoleFormatter, ConsoleFormatterOptions>();
        logging.AddFilter("Microsoft", LogLevel.Warning);
        logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Information);
        return logging;
    }

    public static bool Bool(string? raw, bool fallback) => raw is { Length: > 0 } ? bool.Parse(raw) : fallback;
}
