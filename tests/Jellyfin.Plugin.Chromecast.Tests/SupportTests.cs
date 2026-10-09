using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Chromecast.Cast;
using Jellyfin.Plugin.Chromecast.Session;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Chromecast.Tests;

public class CastTimeoutTests
{
    [Fact]
    public async Task ACallThatAnswersInTimeReturnsItsResult()
    {
        Assert.Equal(42, await Task.FromResult(42).WithTimeoutAsync(TimeSpan.FromSeconds(5), "Test"));
    }

    [Fact]
    public async Task ACallThatNeverAnswersTimesOut()
    {
        var never = new TaskCompletionSource<int>().Task;

        var ex = await Assert.ThrowsAsync<TimeoutException>(() => never.WithTimeoutAsync(TimeSpan.FromMilliseconds(50), "Pausing"));
        Assert.Contains("Pausing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACallThatFailsKeepsItsOwnError()
    {
        var failing = Task.FromException(new InvalidOperationException("socket closed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.WithTimeoutAsync(TimeSpan.FromSeconds(5), "Test"));
    }
}

public class FlexibleGuidConverterTests
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new FlexibleGuidConverter() } };

    // The receiver reports ids without dashes, which System.Text.Json rejects by default - that
    // once made every status broadcast fail to parse.
    [Theory]
    [InlineData("\"b565cf7176b943ceb165fece9b26181f\"")]
    [InlineData("\"b565cf71-76b9-43ce-b165-fece9b26181f\"")]
    public void BothGuidFormatsAreAccepted(string json)
    {
        Assert.Equal(Guid.Parse("b565cf71-76b9-43ce-b165-fece9b26181f"), JsonSerializer.Deserialize<Guid>(json, Options));
    }

    [Fact]
    public void AnEmptyStringIsTheEmptyGuid()
    {
        Assert.Equal(Guid.Empty, JsonSerializer.Deserialize<Guid>("\"\"", Options));
    }
}

public class VerboseGatedLoggerTests
{
    private sealed class Recorder : ILogger<VerboseGatedLoggerTests>
    {
        public List<LogLevel> Written { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Written.Add(logLevel);
    }

    private static List<LogLevel> Write(bool routineInfoIsVerbose, bool verbose)
    {
        var recorder = new Recorder();
        var logger = new VerboseGatedLogger<VerboseGatedLoggerTests>(recorder, routineInfoIsVerbose, () => verbose);
        foreach (var level in new[] { LogLevel.Trace, LogLevel.Debug, LogLevel.Information, LogLevel.Warning, LogLevel.Error })
        {
            logger.Log(level, default, "message", null, static (s, _) => s);
        }

        return recorder.Written;
    }

    [Fact]
    public void ANoisySourceOnlyWritesWarningsAndErrorsByDefault()
    {
        Assert.Equal([LogLevel.Warning, LogLevel.Error], Write(routineInfoIsVerbose: true, verbose: false));
    }

    [Fact]
    public void AQuietSourceKeepsItsInformationByDefault()
    {
        Assert.Equal([LogLevel.Information, LogLevel.Warning, LogLevel.Error], Write(routineInfoIsVerbose: false, verbose: false));
    }

    [Fact]
    public void VerboseAddsDebugAsInformationButNeverTrace()
    {
        Assert.Equal(
            [LogLevel.Information, LogLevel.Information, LogLevel.Warning, LogLevel.Error],
            Write(routineInfoIsVerbose: true, verbose: true));
    }
}
