using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using MsLogger = Microsoft.Extensions.Logging.ILogger;

namespace ZScheme.LanguageServer;

/// <summary>
///     Diagnostic logging for the language server. Everything goes to <b>stderr</b>:
///     stdout is the JSON-RPC channel and a single stray byte there corrupts the
///     protocol. Editors surface a server's stderr in their own logs (Zed writes it to
///     <c>~/.local/share/zed/logs/Zed.log</c>), which is the only way a user can see why
///     analysis failed.
/// </summary>
internal static class StderrLogging
{
    private const string Template =
        "[{Timestamp:HH:mm:ss.fff} {Level:u3}] zs-lsp: {Message:lj}{NewLine}{Exception}";

    /// <summary>Configures Serilog (which the compiler logs through) for the process.
    ///     Warnings and errors are always reported; <c>--debug</c> opens it up to the
    ///     compiler's verbose tracing, matching the <c>zs</c> CLI's own flag.</summary>
    public static void Configure(bool debug)
    {
        var config = new LoggerConfiguration().WriteTo.Console(
            outputTemplate: Template,
            standardErrorFromLevel: LogEventLevel.Verbose
        );

        Log.Logger = (debug ? config.MinimumLevel.Debug() : config.MinimumLevel.Warning())
            .CreateLogger();
    }

    /// <summary>Bridges OmniSharp's <see cref="MsLogger" /> output onto the same stderr
    ///     sink. Without this, an exception thrown inside a notification handler (such as
    ///     <c>textDocument/didOpen</c>) is swallowed with no trace anywhere.</summary>
    public static void AddStderr(ILoggingBuilder builder, bool debug)
    {
        builder.SetMinimumLevel(debug ? LogLevel.Debug : LogLevel.Warning);
        builder.AddProvider(new SerilogForwardingProvider());
    }

    private sealed class SerilogForwardingProvider : ILoggerProvider
    {
        public MsLogger CreateLogger(string categoryName)
        {
            return new SerilogForwardingLogger(categoryName);
        }

        public void Dispose() { }
    }

    /// <summary>Scopes carry no extra context here; the sink is flat.</summary>
    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose() { }
    }

    private sealed class SerilogForwardingLogger(string category) : MsLogger
    {
        IDisposable MsLogger.BeginScope<TState>(TState state)
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return Serilog.Log.IsEnabled(ToSerilog(logLevel));
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (!IsEnabled(logLevel))
                return;
            Serilog.Log.Write(
                ToSerilog(logLevel),
                exception,
                "{Category}: {Message}",
                category,
                formatter(state, exception)
            );
        }

        private static LogEventLevel ToSerilog(LogLevel level)
        {
            return level switch
            {
                LogLevel.Trace => LogEventLevel.Verbose,
                LogLevel.Debug => LogEventLevel.Debug,
                LogLevel.Information => LogEventLevel.Information,
                LogLevel.Warning => LogEventLevel.Warning,
                LogLevel.Error => LogEventLevel.Error,
                _ => LogEventLevel.Fatal,
            };
        }
    }
}
