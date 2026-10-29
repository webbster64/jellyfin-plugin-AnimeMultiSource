using System;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AnimeMultiSource
{
    // Copies every plugin log line into its own daily file (AnimeMultiSource_yyyy-MM-dd.log next to Jellyfin's logs),
    // at the level picked in the plugin settings. Jellyfin logs through Serilog, which ignores providers a plugin
    // registers and drops Debug unless logging.json is edited, so the plugin's loggers are wrapped instead.
    public static class PluginLog
    {
        private const int KeepDays = 7;
        private static readonly object FileLock = new();
        private static string? _currentFile;

        // Set by the Plugin constructor, which Jellyfin runs before any provider or task is created.
        public static string? Directory { get; set; }

        public static ILogger<T> Wrap<T>(ILogger<T> inner) => inner is FileLogger<T> ? inner : new FileLogger<T>(inner);

        public static bool ShouldWrite(LogLevel level, bool debug) =>
            level != LogLevel.None && level >= (debug ? LogLevel.Debug : LogLevel.Information);

        private static bool FileEnabled(LogLevel level) =>
            Directory != null && ShouldWrite(level, Plugin.Instance?.Configuration.DebugLogging == true);

        private static void Write(string category, LogLevel level, string message, Exception? exception)
        {
            var now = DateTimeOffset.Now;
            var line = $"[{now:yyyy-MM-dd HH:mm:ss.fff zzz}] [{Abbreviation(level)}] {category}: {message}{Environment.NewLine}";
            if (exception != null)
            {
                line += exception + Environment.NewLine;
            }

            try
            {
                lock (FileLock)
                {
                    var file = Path.Combine(Directory!, $"AnimeMultiSource_{now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.log");
                    if (file != _currentFile)
                    {
                        _currentFile = file;
                        DeleteOldFiles(now);
                    }

                    // shortcut: opens the file per line, fine at plugin log volume; buffer it if Debug ever gets hot
                    File.AppendAllText(file, line);
                }
            }
            catch (Exception)
            {
                // The file is a convenience; a full disk or bad permissions must never break a metadata refresh.
            }
        }

        private static void DeleteOldFiles(DateTimeOffset now)
        {
            foreach (var old in System.IO.Directory.GetFiles(Directory!, "AnimeMultiSource_*.log"))
            {
                if (File.GetLastWriteTime(old) < now.LocalDateTime.AddDays(-KeepDays))
                {
                    File.Delete(old);
                }
            }
        }

        private static string Abbreviation(LogLevel level) => level switch
        {
            LogLevel.Trace => "VRB",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "FTL",
        };

        private sealed class FileLogger<T> : ILogger<T>
        {
            private static readonly string Category = typeof(T).FullName ?? typeof(T).Name;
            private readonly ILogger<T> _inner;

            public FileLogger(ILogger<T> inner) => _inner = inner;

            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);

            public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel) || FileEnabled(logLevel);

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (_inner.IsEnabled(logLevel))
                {
                    _inner.Log(logLevel, eventId, state, exception, formatter);
                }

                if (FileEnabled(logLevel))
                {
                    Write(Category, logLevel, formatter(state, exception), exception);
                }
            }
        }
    }
}
