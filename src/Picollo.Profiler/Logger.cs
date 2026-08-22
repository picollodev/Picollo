// Logger.cs

using System;
using System.IO;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZLogger;
using ZLogger.Providers;

namespace Picollo.Profiler;

public static class Logger
{
    private static readonly MutableFilterOptions Filter = new(LogLevel.Information);

    private static readonly LoggerFactory s_factory = new([], Filter);

    public static ILogger ForType<T>() => Factory.CreateLogger<T>();
    public static ILogger ForType(Type type) => Factory.CreateLogger(type);

    public static ILoggerFactory Factory => s_factory;

    public static void SetLevel(LogLevel level) => Filter.SetLevel(level);

    public static void AddConsole()
    {
        Console.OutputEncoding = new UTF8Encoding(false);

        var options = new ZLoggerConsoleOptions
        {
            OutputEncodingToUtf8 = true,
            ConfigureEnableAnsiEscapeCode = true
        };

        ConfigureColoredConsoleFormat(options);

        s_factory.AddProvider(new ZLoggerConsoleLoggerProvider(options));
    }

    public static void AddFile(string sessionDir)
    {
        var path = Path.Combine(sessionDir, "profiler.log");

        var options = new ZLoggerFileOptions
        {
            FileShared = true,
            InternalErrorLogger = ex => Console.Error.WriteLine(ex),
            CaptureThreadInfo = true
        };

        ConfigurePlainFormat(options);

        s_factory.AddProvider(new ZLoggerFileLoggerProvider(path, options));
    }

    public static void ConfigureSession(string? sessionDir, bool console, LogLevel level = LogLevel.Debug)
    {
        try
        {
            SetLevel(level);

            if (!string.IsNullOrWhiteSpace(sessionDir))
                AddFile(sessionDir);
            
            if (console)
                AddConsole();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"{ex.Message}\n{ex.StackTrace}");
        }
    }

    public static void Shutdown() => s_factory.Dispose();

    private static void ConfigurePlainFormat(ZLoggerOptions options)
    {
        options.UsePlainTextFormatter(formatter =>
        {
            formatter.SetPrefixFormatter(
                $"{0:HH:mm:ss.fff} [{1:short}] {2}: ",
                (in template, in info) =>
                    template.Format(info.Timestamp.Utc, info.LogLevel, info.Category));
        });
    }
    
    private static void ConfigureColoredConsoleFormat(ZLoggerOptions options)
    {
        options.UsePlainTextFormatter(formatter =>
        {
            formatter.SetPrefixFormatter(
                $"{0}{1:HH:mm:ss.fff} [{2:short}] {3}: {4}",
                (in template, in info) =>
                {
                    var color = info.LogLevel switch
                    {
                        LogLevel.Trace => "\u001b[90m",      // dark grey
                        LogLevel.Debug => "\u001b[37m",      // grey / light grey
                        LogLevel.Information => "\u001b[32m", // green
                        LogLevel.Warning => "\u001b[33m",     // yellow
                        LogLevel.Error => "\u001b[31m",       // red
                        LogLevel.Critical => "\u001b[1;31m",  // bright/bold red
                        _ => ""                               // no color
                    };

                    template.Format(
                        color,
                        info.Timestamp.Utc,
                        info.LogLevel,
                        info.Category,
                        "\u001b[0m"); // reset
                });
        });
    }

    private sealed class MutableFilterOptions : IOptionsMonitor<LoggerFilterOptions>
    {
        private LoggerFilterOptions _current;
        private event Action<LoggerFilterOptions, string?>? Changed;

        public MutableFilterOptions(LogLevel level)
        {
            _current = new LoggerFilterOptions
            {
                MinLevel = level
            };
        }

        public LoggerFilterOptions CurrentValue => Volatile.Read(ref _current);

        public LoggerFilterOptions Get(string? name) => CurrentValue;

        public IDisposable OnChange(Action<LoggerFilterOptions, string?> listener)
        {
            Changed += listener;
            return new Subscription(this, listener);
        }

        // ReSharper disable once MemberHidesStaticFromOuterClass
        public void SetLevel(LogLevel level)
        {
            var next = new LoggerFilterOptions
            {
                MinLevel = level
            };
            Volatile.Write(ref _current, next);
            Changed?.Invoke(next, null);
        }

        private sealed class Subscription : IDisposable
        {
            private MutableFilterOptions? _owner;
            private readonly Action<LoggerFilterOptions, string?> _listener;

            public Subscription(
                MutableFilterOptions owner,
                Action<LoggerFilterOptions, string?> listener)
            {
                _owner = owner;
                _listener = listener;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner != null)
                    owner.Changed -= _listener;
            }
        }
    }
}