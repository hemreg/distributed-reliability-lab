using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace Dependency;

/// <summary>
/// One flat JSON object per line: ts, level, category, scope fields, message-template fields, msg.
/// Fields such as phase, run_id and window_start_s become top-level keys, which keeps the
/// logs greppable and easy to filter with jq.
/// </summary>
public sealed class CompactJsonConsoleFormatter() : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "e03-json";

    // Console output only, never embedded in HTML: relaxed escaping keeps lines readable.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);

        // Only the app's own scopes (plain dictionaries). ASP.NET's
        // hosting/connection scopes (TraceId, SpanId, ConnectionId, ...) are left out to keep lines short.
        scopeProvider?.ForEachScope((scope, state) =>
        {
            if (scope is Dictionary<string, object?> pairs)
                foreach (var (key, value) in pairs) state[key] = value;
        }, fields);

        if (logEntry.State is IEnumerable<KeyValuePair<string, object?>> statePairs)
            foreach (var (key, value) in statePairs)
                if (key != "{OriginalFormat}") fields[key] = value;

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, WriterOptions))
        {
            json.WriteStartObject();
            json.WriteString("ts", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
            json.WriteString("level", logEntry.LogLevel switch
            {
                LogLevel.Trace => "trace",
                LogLevel.Debug => "debug",
                LogLevel.Information => "info",
                LogLevel.Warning => "warn",
                LogLevel.Error => "error",
                LogLevel.Critical => "critical",
                _ => "none",
            });
            json.WriteString("category", logEntry.Category);
            foreach (var (key, value) in fields)
            {
                switch (value)
                {
                    case bool b: json.WriteBoolean(key, b); break;
                    case int i: json.WriteNumber(key, i); break;
                    case long l: json.WriteNumber(key, l); break;
                    case double d: json.WriteNumber(key, d); break;
                    default: json.WriteString(key, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)); break;
                }
            }
            json.WriteString("msg", logEntry.Formatter(logEntry.State, logEntry.Exception));
            if (logEntry.Exception is { } ex)
                json.WriteString("error", $"{ex.GetType().Name}: {ex.Message}");
            json.WriteEndObject();
        }

        textWriter.Write(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        textWriter.Write(Environment.NewLine);
    }
}
