// Logger.cs

using System;
using System.Collections.Generic;
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
    private static readonly Lock FactoryLock = new();
    private static readonly List<ForwardingLogger> Loggers = [];

    private static volatile ILoggerFactory _factory = new LoggerFactory([], Filter);

    public static ILogger ForType<T>() => ForType(typeof(T));
    public static ILogger ForType(Type type)
    {
        string categoryName = type.FullName ?? type.Name;
        lock (FactoryLock)
        {
            var logger = new ForwardingLogger(categoryName, _factory.CreateLogger(categoryName));
            Loggers.Add(logger);
            return logger;
        }
    }

    public static ILoggerFactory Factory => _factory;

    public static void SetLevel(LogLevel level) => Filter.SetLevel(level);

    private static void AddConsole(ILoggerFactory factory)
    {
        Console.OutputEncoding = new UTF8Encoding(false);

        var options = new ZLoggerConsoleOptions
        {
            OutputEncodingToUtf8 = true,
            ConfigureEnableAnsiEscapeCode = true
        };

        ConfigureColoredConsoleFormat(options);

        factory.AddProvider(new ZLoggerConsoleLoggerProvider(options));
    }

    private static void AddFile(ILoggerFactory factory, string sessionDir)
    {
        var path = Path.Combine(sessionDir, "profiler.log");

        var options = new ZLoggerFileOptions
        {
            FileShared = true,
            InternalErrorLogger = ex => Console.Error.WriteLine(ex),
            CaptureThreadInfo = true
        };

        ConfigurePlainFormat(options);

        factory.AddProvider(new ZLoggerFileLoggerProvider(path, options));
    }

    public static void ConfigureSession(string? sessionDir, bool console, LogLevel level = LogLevel.Debug)
    {
        ILoggerFactory? nextFactory = null;
        try
        {
            SetLevel(level);
            nextFactory = new LoggerFactory([], Filter);

            if (!string.IsNullOrWhiteSpace(sessionDir))
                AddFile(nextFactory, sessionDir);
            
            if (console)
                AddConsole(nextFactory);

            ILoggerFactory previousFactory;
            lock (FactoryLock)
            {
                previousFactory = _factory;
                _factory = nextFactory;
                foreach (ForwardingLogger logger in Loggers)
                    logger.SetFactory(nextFactory);

                nextFactory = null;
            }

            previousFactory.Dispose();
        }
        catch (Exception ex)
        {
            nextFactory?.Dispose();
            Console.WriteLine($"{ex.Message}\n{ex.StackTrace}");
        }
    }

    public static void Shutdown()
    {
        var emptyFactory = new LoggerFactory([], Filter);
        ILoggerFactory sessionFactory;
        lock (FactoryLock)
        {
            sessionFactory = _factory;
            _factory = emptyFactory;
            foreach (ForwardingLogger logger in Loggers)
                logger.SetFactory(emptyFactory);
        }

        sessionFactory.Dispose();
    }

    private sealed class ForwardingLogger(string categoryName, ILogger logger) : ILogger
    {
        private volatile ILogger _logger = logger;

        public void SetFactory(ILoggerFactory factory) =>
            _logger = factory.CreateLogger(categoryName);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            _logger.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => _logger.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            _logger.Log(logLevel, eventId, state, exception, formatter);
    }

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
        private volatile LoggerFilterOptions _current;
        private event Action<LoggerFilterOptions, string?>? Changed;

        public MutableFilterOptions(LogLevel level)
        {
            _current = new LoggerFilterOptions
            {
                MinLevel = level
            };
        }

        public LoggerFilterOptions CurrentValue => _current;

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
            _current = next;
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
