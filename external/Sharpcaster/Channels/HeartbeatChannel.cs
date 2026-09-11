using Microsoft.Extensions.Logging;
using Sharpcaster.Extensions;
using Sharpcaster.Messages.Heartbeat;
using System;
using System.Text.Json;
using System.Timers;

namespace Sharpcaster.Channels
{
    /// <summary>
    /// Heartbeat channel. Responds to ping messages with pong message
    /// </summary>
    public class HeartbeatChannel : ChromecastChannel, IDisposable
    {
        //private ILogger _logger = null;
        private readonly Timer _timer;
        private bool disposedValue;
        private bool _triedToPing;

        private static readonly Action<ILogger, Exception?> LogPongSent =
            LoggerMessage.Define(LogLevel.Debug, new EventId(1001, nameof(OnMessageReceived)), "Pong sent - Heartbeat Timer restarted.");

        private static readonly Action<ILogger, Exception?> LogHeartbeatTimerStarted =
            LoggerMessage.Define(LogLevel.Trace, new EventId(1002, nameof(StartTimeoutTimer)), "Started heartbeat timeout timer");

        private static readonly Action<ILogger, Exception?> LogHeartbeatTimerStopped =
            LoggerMessage.Define(LogLevel.Trace, new EventId(1003, nameof(StopTimeoutTimer)), "Stopped heartbeat timeout timer");

        private static readonly Action<ILogger, Exception?> LogHeartbeatTimeout =
            LoggerMessage.Define(LogLevel.Information, new EventId(1004, nameof(TimerElapsed)), "Heartbeat timeout");

        private static readonly Action<ILogger, Exception?> LogHeartbeatTimerRestarted =
            LoggerMessage.Define(LogLevel.Trace, new EventId(1005, nameof(StartTimeoutTimer)), "Restarted heartbeat timeout timer");

        private static readonly Action<ILogger, Exception?> LogPongReceived =
            LoggerMessage.Define(LogLevel.Debug, new EventId(1006, nameof(OnMessageReceived)), "Pong received - Heartbeat Timer restarted.");

        private static readonly Action<ILogger, Exception?> LogPingSent =
            LoggerMessage.Define(LogLevel.Debug, new EventId(1008, nameof(OnMessageReceived)), "Ping sent - Heartbeat Timer restarted.");

        /// <summary>
        /// Initializes a new instance of HeartbeatChannel class
        /// </summary>
        public HeartbeatChannel(ILogger? logger = null) : base("tp.heartbeat", logger)
        {
            _timer = new Timer(10000); // timeout is 10 seconds.
                                       // Normally Chromecast application only waits for 8 seconds before it tries to ping us.
                                       // So we give it a bit more time.
            _timer.Elapsed += TimerElapsed;
            _timer.AutoReset = false;
        }

        public event EventHandler? StatusChanged;

        /// <summary>
        /// Patch: an extra CastV2 destination id to also send heartbeat ping/pong traffic to,
        /// alongside the default "receiver-0" platform destination. Heartbeat traffic addressed
        /// only to "receiver-0" was not enough on its own to keep a separate virtual connection
        /// to a *launched app's* transport id alive - confirmed by testing, a connection to the
        /// Jellyfin Chromecast plugin's receiver app was reliably torn down ~20-30s after launch
        /// even with heartbeat responding correctly on "receiver-0". Set this to the app's
        /// transport id once known. See PATCH.md.
        /// </summary>
        public string? AdditionalDestinationId { get; set; }

        /// <summary>
        /// Called when a message for this channel is received
        /// </summary>
        /// <param name="messagePayload">message payload to process</param>
        /// <param name="type">message type</param>
        public override async void OnMessageReceived(string messagePayload, string type)
        {
            if (type == "PONG")
            {
                _triedToPing = false;
                _timer.Stop();
                _timer.Start();
                if (Logger != null) LogPongReceived(Logger, null);
                return;
            }
            _timer.Stop();
            var pongJson = JsonSerializer.Serialize(new PongMessage(), SharpcasteSerializationContext.Default.PongMessage);
            await SendAsync(pongJson).ConfigureAwait(false);
            if (AdditionalDestinationId is not null)
            {
                await SendAsync(pongJson, AdditionalDestinationId).ConfigureAwait(false);
            }

            _timer.Start();
            if (Logger != null) LogPongSent(Logger, null);
        }

        public void StartTimeoutTimer()
        {
            _timer.Start();
            if (Logger != null) LogHeartbeatTimerStarted(Logger, null);
        }

        public void StopTimeoutTimer()
        {
            _timer.Stop();
            if (Logger != null) LogHeartbeatTimerStopped(Logger, null);
        }

        public void RestartTimeoutTimer()
        {
            _triedToPing = false;
            _timer.Stop();
            _timer.Start();
            if (Logger != null) LogHeartbeatTimerRestarted(Logger, null);
        }

        private async void TimerElapsed(object sender, ElapsedEventArgs e)
        {
            if (_triedToPing)
            {
                if (Logger != null) LogHeartbeatTimeout(Logger, null);
                SafeInvokeEvent(StatusChanged, this, e);
                return;
            }
            var pingJson = JsonSerializer.Serialize(new PingMessage(), SharpcasteSerializationContext.Default.PingMessage);
            await SendAsync(pingJson).ConfigureAwait(false);
            if (AdditionalDestinationId is not null)
            {
                await SendAsync(pingJson, AdditionalDestinationId).ConfigureAwait(false);
            }

            _triedToPing = true;
            // Patch: _timer.AutoReset is false, so without restarting it here the timer never
            // fires again after this one unanswered ping - the "did the device actually respond"
            // check on the next elapse (the _triedToPing branch above) would never run, silently
            // disabling heartbeat timeout detection for the rest of the connection's life. See
            // PATCH.md.
            _timer.Start();
            if (Logger != null) LogPingSent(Logger, null);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    _timer?.Dispose();
                }
                disposedValue = true;
            }
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
