using System.Text.Json;
using RabbitMQ.Client;
using RaceNetScraper.Shared.Messaging;

namespace RaceNetScraper.App.Services;

/// <summary>
/// Publishes <see cref="MeetingScrapedEvent"/>s to RabbitMQ for the desktop app. Plain class (this
/// app has no DI container, no hosted services, and no ILogger — see <see cref="AppSettings"/> and
/// the static S3JsonUploader helper for the same style): it owns one long-lived,
/// auto-recovering <see cref="IConnection"/> created lazily on the first publish, and reports
/// outcomes through the same <see cref="IProgress{T}"/>/StatusText channel the UI already reads for
/// S3 results — not a log file.
///
/// Non-fatal by contract: no method here ever throws. A publish failure or an unreachable broker
/// is reported and counted, and the scrape carries on exactly as it does today.
///
/// Topology ownership is split: this app only asserts the (durable, topic) exchange. It never
/// declares, binds, or sets arguments on the queue — the <c>x-max-priority</c> queue is owned by
/// the separate priority-control project. Messages are always published at the configured default
/// priority; this app never re-prioritizes.
/// </summary>
public sealed class RabbitMqMeetingEventPublisher : IMeetingEventPublisher, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private IConnection? _connection;
    private bool _disposed;

    public RabbitMqMeetingEventPublisher(AppSettings settings) => _settings = settings;

    /// <summary>Shared-contract entry point (no progress reporting). Delegates to the richer
    /// overload the desktop app actually calls.</summary>
    async Task IMeetingEventPublisher.PublishMeetingScrapedAsync(MeetingScrapedEvent evt, CancellationToken ct) =>
        await PublishMeetingScrapedAsync(evt, progress: null, ct);

    /// <summary>Publishes one event. Never throws — returns true if the broker accepted the
    /// message, false if it couldn't be published (broker down, transient error after retries).
    /// A one-off retry covers a dropped connection; anything past that is reported and counted as
    /// a failure so the scrape can fold it into its status summary.</summary>
    public async Task<bool> PublishMeetingScrapedAsync(
        MeetingScrapedEvent evt, IProgress<string>? progress, CancellationToken ct)
    {
        if (_disposed) return false;

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                await PublishOnceAsync(evt, progress, ct);
                return true;
            }
            catch (Exception ex)
            {
                lastError = ex;
                // A stale/half-open connection is the usual transient cause — drop it so the retry
                // rebuilds one from scratch rather than reusing the broken handle.
                await ResetConnectionAsync();
                if (attempt < 2)
                {
                    try { await Task.Delay(500, ct); }
                    catch (OperationCanceledException) { return false; }
                }
            }
        }

        progress?.Report($"RabbitMQ publish failed for {evt.MeetingName}: {lastError?.Message}");
        return false;
    }

    private async Task PublishOnceAsync(MeetingScrapedEvent evt, IProgress<string>? progress, CancellationToken ct)
    {
        var connection = await EnsureConnectionAsync(ct);

        // Publisher confirmations on, so awaiting BasicPublishAsync actually waits for the broker to
        // accept the message rather than returning the instant it's buffered. Channels aren't
        // thread-safe to share and are cheap at this volume (a handful of meetings per run), so we
        // open a fresh short-lived one per publish.
        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);
        await using var channel = await connection.CreateChannelAsync(channelOptions, ct);

        // mandatory: true asks the broker to return the message if nothing is bound to route it.
        // Before the consumer/priority project's queue exists (or during its rollout) that's an
        // expected state, not an error — surface it as a note and still count the publish as
        // succeeded, since the message did reach the exchange.
        channel.BasicReturnAsync += (_, args) =>
        {
            progress?.Report(
                $"RabbitMQ event for {evt.MeetingName} was not routed to any queue " +
                $"(reply {args.ReplyCode} {args.ReplyText}) — expected until a consumer binds.");
            return Task.CompletedTask;
        };

        await channel.ExchangeDeclareAsync(
            exchange: _settings.RabbitMqExchangeName,
            type: ExchangeType.Topic,
            durable: true,
            autoDelete: false,
            arguments: null,
            cancellationToken: ct);

        var routingKey = _settings.RabbitMqRoutingKeyTemplate
            .Replace("{source}", _settings.RabbitMqSource)
            .Replace("{disciplineCode}", (evt.DisciplineCode ?? "").ToLowerInvariant());

        var body = JsonSerializer.SerializeToUtf8Bytes(evt, JsonOptions);

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            Priority = (byte)evt.Priority,
            MessageId = evt.EventId,
            Type = evt.EventType,
            AppId = evt.Source,
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        };

        await channel.BasicPublishAsync(
            exchange: _settings.RabbitMqExchangeName,
            routingKey: routingKey,
            mandatory: true,
            basicProperties: properties,
            body: body,
            cancellationToken: ct);
    }

    private async Task<IConnection> EnsureConnectionAsync(CancellationToken ct)
    {
        if (_connection is { IsOpen: true }) return _connection;

        await _connectionGate.WaitAsync(ct);
        try
        {
            if (_connection is { IsOpen: true }) return _connection;

            var factory = new ConnectionFactory
            {
                HostName = _settings.RabbitMqHostName,
                Port = _settings.RabbitMqPort,
                VirtualHost = _settings.RabbitMqVirtualHost,
                UserName = _settings.RabbitMqUserName,
                Password = _settings.RabbitMqPassword,
                AutomaticRecoveryEnabled = true,
                TopologyRecoveryEnabled = true,
            };

            _connection = await factory.CreateConnectionAsync(ct);
            return _connection;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async Task ResetConnectionAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
            if (_connection is not null)
            {
                try { await _connection.DisposeAsync(); }
                catch { /* best effort — we're discarding it anyway */ }
                _connection = null;
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>Closes the connection on app shutdown, bounded by a short timeout so exit can never
    /// hang waiting on an unreachable broker.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        var connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            var closeTask = connection.DisposeAsync().AsTask();
            await Task.WhenAny(closeTask, Task.Delay(TimeSpan.FromSeconds(3)));
        }

        _connectionGate.Dispose();
    }
}
