using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace KuraiTransferTracker.Logging;

/// <summary>
/// Proveedor de logs en archivo de texto diario dentro de la carpeta "logs/".
/// Genera un archivo por día: logs/kurai-tracker-YYYY-MM-DD.log
/// </summary>
public sealed class DailyFileLoggerProvider : ILoggerProvider
{
    private readonly string _logsDirectory;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, DailyFileLogger> _loggers = new();

    public DailyFileLoggerProvider(string logsDirectory)
    {
        _logsDirectory = logsDirectory;
        Directory.CreateDirectory(_logsDirectory);
    }

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, name => new DailyFileLogger(name, _logsDirectory, _writeLock));

    public void Dispose() => _loggers.Clear();

    private sealed class DailyFileLogger : ILogger
    {
        private readonly string _categoryName;
        private readonly string _logsDirectory;
        private readonly object _writeLock;

        public DailyFileLogger(string categoryName, string logsDirectory, object writeLock)
        {
            _categoryName = categoryName;
            _logsDirectory = logsDirectory;
            _writeLock = writeLock;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            DateTime now = DateTime.Now;
            string filePath = Path.Combine(_logsDirectory, $"kurai-tracker-{now:yyyy-MM-dd}.log");
            string levelTag = logLevel switch
            {
                LogLevel.Information => "INFO ",
                LogLevel.Warning => "WARN ",
                LogLevel.Error => "ERROR",
                LogLevel.Critical => "CRIT ",
                _ => logLevel.ToString().ToUpperInvariant()
            };

            string message = formatter(state, exception);
            var sb = new StringBuilder();
            sb.Append($"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{levelTag}] [{_categoryName}] {message}");

            if (exception != null)
            {
                sb.AppendLine();
                sb.Append(exception);
            }

            sb.AppendLine();

            lock (_writeLock)
            {
                Directory.CreateDirectory(_logsDirectory);
                File.AppendAllText(filePath, sb.ToString(), Encoding.UTF8);
            }
        }
    }
}
