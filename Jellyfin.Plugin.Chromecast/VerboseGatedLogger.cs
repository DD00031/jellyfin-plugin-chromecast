using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Chromecast;

/// <summary>
/// Ties log output to the plugin's "Verbose logging" setting, read on every call so it applies
/// without a restart. With verbose off, Debug/Trace are dropped - and so is routine Information
/// when <c>routineInfoIsVerbose</c> is set (SharpCaster's locator logs several Information lines
/// per discovery scan, i.e. every 30 seconds forever). With verbose on, Debug is written at Information
/// so it shows up without editing Jellyfin's logging configuration; Trace never is.
/// </summary>
/// <typeparam name="T">The log category.</typeparam>
internal sealed class VerboseGatedLogger<T> : ILogger<T>
{
    private readonly ILogger<T> _inner;
    private readonly bool _routineInfoIsVerbose;

    public VerboseGatedLogger(ILogger<T> inner, bool routineInfoIsVerbose)
    {
        _inner = inner;
        _routineInfoIsVerbose = routineInfoIsVerbose;
    }

    private static bool Verbose => Plugin.Instance?.Configuration.EnableDebugLogging ?? false;

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => _inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel)
        => logLevel switch
        {
            >= LogLevel.Warning => _inner.IsEnabled(logLevel),
            LogLevel.Information => (!_routineInfoIsVerbose || Verbose) && _inner.IsEnabled(logLevel),
            LogLevel.Debug => Verbose,

            // Trace is every heartbeat and every raw frame, several lines a second - never promoted.
            _ => false
        };

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            _inner.Log(logLevel < LogLevel.Information ? LogLevel.Information : logLevel, eventId, state, exception, formatter);
        }
    }
}
