using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using CrawlForge.Contracts.Api;
using CrawlForge.Contracts.Enums;
using CrawlForge.Contracts.Persistence;
using CrawlForge.Contracts.Security;

namespace CrawlForge.Export.Delivery;

/// <summary>
/// Delivery to user-configured export destinations (FS-CRAWL-001 §8.5,
/// locked <see cref="IExportDestinationDelivery"/>). Destinations are off by
/// default and none are seeded: a disabled or absent destination produces a
/// failure verdict WITHOUT any network attempt (criterion #14). Transfers
/// target only the user's own endpoints (NET-02 boundary class). Credentials
/// are decrypted at the point of use via the locked
/// <see cref="ISecretProtector"/> and never logged or returned in clear.
/// <para>
/// State machine: delivering → delivered | retrying → … → failed, with
/// bounded exponential backoff; every transition is emitted on the
/// <c>export-delivery</c> hub event and recorded in the in-memory ledger.
/// Delivery failures never fail the audit or the local export.
/// </para>
/// <para>
/// <b>Restart semantics:</b> the ledger is runtime state and is lost on
/// process restart; exported files and destination rows are durable.
/// </para>
/// </summary>
public sealed class ExportDestinationDelivery : IExportDestinationDelivery
{
    private readonly IDbProvider _provider;
    private readonly ISecretProtector _protector;
    private readonly ExportJobRegistry _jobs;
    private readonly ExportEventPublisher _events;
    private readonly ExportOptions _options;
    private readonly Func<ExportDestinationClass, IExportTransport> _transportFactory;
    private readonly ConcurrentDictionary<(long JobId, long DestinationId), DeliveryLedgerEntry> _ledger = new();

    /// <summary>Creates the delivery service.</summary>
    /// <param name="provider">Database provider (locked contract).</param>
    /// <param name="protector">Secret protector (locked contract).</param>
    /// <param name="jobs">Export job registry (shared with the engine).</param>
    /// <param name="events">Hub event publisher.</param>
    /// <param name="options">Export options (retry bounds).</param>
    /// <param name="transportFactory">Transport factory override (tests); null uses the real transports.</param>
    public ExportDestinationDelivery(
        IDbProvider provider,
        ISecretProtector protector,
        ExportJobRegistry jobs,
        ExportEventPublisher events,
        ExportOptions? options = null,
        Func<ExportDestinationClass, IExportTransport>? transportFactory = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(events);
        _provider = provider;
        _protector = protector;
        _jobs = jobs;
        _events = events;
        _options = options ?? ExportOptions.Default;
        _transportFactory = transportFactory ?? DefaultTransportFactory;
    }

    /// <summary>Reads one ledger entry (runtime state; null when never attempted).</summary>
    /// <param name="exportJobId">Export job identifier.</param>
    /// <param name="destinationId">Destination identifier.</param>
    /// <returns>The ledger entry, or null.</returns>
    public DeliveryLedgerEntry? GetLedgerEntry(long exportJobId, long destinationId)
        => _ledger.TryGetValue((exportJobId, destinationId), out var entry) ? entry : null;

    /// <inheritdoc/>
    public async Task<DestinationTestResult> TestAsync(long destinationId, CancellationToken cancellationToken = default)
    {
        var destination = await LoadDestinationAsync(destinationId, cancellationToken).ConfigureAwait(false);
        if (destination is null)
        {
            return new DestinationTestResult(false, false, "Destination not found.");
        }

        if (!destination.IsEnabled)
        {
            // Disabled destinations never touch the network (criterion #14).
            return new DestinationTestResult(false, false, "Destination is disabled.");
        }

        try
        {
            var endpoint = DestinationEndpoint.Parse(destination.EndpointDetailsJson);
            endpoint.Validate(destination.Class);
            var credentials = DestinationCredentials.Unprotect(_protector, destination.EncryptedCredentialsJson);
            var transport = _transportFactory(destination.Class);
            var probe = await transport.TestAsync(endpoint, credentials, cancellationToken).ConfigureAwait(false);
            return new DestinationTestResult(probe.IsReachable, probe.CredentialsValid, probe.Message);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException)
        {
            return new DestinationTestResult(false, false, ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task DeliverAsync(long exportJobId, long destinationId, CancellationToken cancellationToken = default)
    {
        var job = _jobs.Get(exportJobId)
            ?? throw new InvalidOperationException($"Export job {exportJobId} not found.");
        if (job.Status != ExportJobStatus.Completed || job.FilePath is null)
        {
            throw new InvalidOperationException($"Export job {exportJobId} is not completed.");
        }

        var destination = await LoadDestinationAsync(destinationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Destination {destinationId} not found.");
        if (!destination.IsEnabled)
        {
            // Disabled destinations never touch the network (criterion #14).
            await RecordAsync(job.Id, destinationId, ExportDeliveryStates.Failed, 0, "Destination is disabled.", cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Destination {destinationId} is disabled.");
        }

        var endpoint = DestinationEndpoint.Parse(destination.EndpointDetailsJson);
        endpoint.Validate(destination.Class);
        var credentials = DestinationCredentials.Unprotect(_protector, destination.EncryptedCredentialsJson);
        var transport = _transportFactory(destination.Class);

        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= Math.Max(1, _options.MaxDeliveryAttempts); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RecordAsync(job.Id, destinationId, ExportDeliveryStates.Delivering, attempt, null, cancellationToken).ConfigureAwait(false);
            try
            {
                await transport.SendAsync(endpoint, credentials, job.FilePath, cancellationToken).ConfigureAwait(false);
                await RecordAsync(job.Id, destinationId, ExportDeliveryStates.Delivered, attempt, null, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RecordAsync(job.Id, destinationId, ExportDeliveryStates.Failed, attempt, "Cancelled.", cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                lastFailure = ex;
                if (attempt < _options.MaxDeliveryAttempts)
                {
                    await RecordAsync(job.Id, destinationId, ExportDeliveryStates.Retrying, attempt, ex.Message, cancellationToken).ConfigureAwait(false);
                    var delay = TimeSpan.FromTicks(_options.DeliveryRetryBaseDelay.Ticks * (1L << (attempt - 1)));
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await RecordAsync(job.Id, destinationId, ExportDeliveryStates.Failed, _options.MaxDeliveryAttempts, lastFailure?.Message, cancellationToken).ConfigureAwait(false);
        throw new InvalidOperationException($"Delivery to destination {destinationId} failed after {_options.MaxDeliveryAttempts} attempts.", lastFailure);
    }

    private static IExportTransport DefaultTransportFactory(ExportDestinationClass destinationClass) => destinationClass switch
    {
        ExportDestinationClass.LocalFolder => new LocalFolderTransport(),
        ExportDestinationClass.Ftp => new FtpTransport(),
        ExportDestinationClass.Sftp => new SftpTransport(),
        ExportDestinationClass.Webhook => new WebhookTransport(),
        ExportDestinationClass.Other => new WebhookTransport(),
        _ => throw new InvalidOperationException($"Unknown destination class '{destinationClass}'."),
    };

    private async Task RecordAsync(long jobId, long destinationId, string state, int attempt, string? message, CancellationToken cancellationToken)
    {
        var entry = new DeliveryLedgerEntry(jobId, destinationId, state, attempt, message, DateTimeOffset.UtcNow);
        _ledger[(jobId, destinationId)] = entry;

        // G-F fix (WP-09-10): the frozen frontend guard (guards.ts
        // isExportDeliveryPayload) accepts only delivered/retrying/failed with
        // string identifiers. The internal "delivering" transition is ledger
        // state only — publishing it would be dropped by the consumer.
        if (state is ExportDeliveryStates.Delivered or ExportDeliveryStates.Retrying or ExportDeliveryStates.Failed)
        {
            await _events.PublishDeliveryAsync(
                new ExportDeliveryPayload(
                    jobId.ToString(CultureInfo.InvariantCulture),
                    destinationId.ToString(CultureInfo.InvariantCulture),
                    state),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record DestinationRow(ExportDestinationClass Class, string EndpointDetailsJson, string? EncryptedCredentialsJson, bool IsEnabled);

    private async Task<DestinationRow?> LoadDestinationAsync(long destinationId, CancellationToken cancellationToken)
    {
        await using var connection = await _provider.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT `destination_class`, `endpoint_details_json`, `encrypted_credentials_json`, `is_enabled` FROM `export_destinations` WHERE `id` = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = destinationId;
        command.Parameters.Add(parameter);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new DestinationRow(
            (ExportDestinationClass)reader.GetInt32(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetBoolean(3));
    }
}
