using System.Net.Http.Headers;
using CrawlForge.Contracts.Enums;
using FluentFTP;
using Renci.SshNet;

namespace CrawlForge.Export.Delivery;

/// <summary>
/// Transfer mechanics of one destination class (FS-CRAWL-001 §8.5). Every
/// implementation targets ONLY the user-configured endpoint — CrawlForge.Export
/// is an approved NET-02 outbound boundary class; no vendor service exists.
/// The abstraction exists so tests can double the mechanics; the production
/// implementations below are the real transports.
/// </summary>
public interface IExportTransport
{
    /// <summary>Tests reachability and credentials without transferring data.</summary>
    /// <param name="endpoint">Validated user endpoint.</param>
    /// <param name="credentials">User credentials, when configured.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The reachability/credential verdict.</returns>
    Task<DestinationProbe> TestAsync(DestinationEndpoint endpoint, DestinationCredentials? credentials, CancellationToken cancellationToken);

    /// <summary>Transfers one export file to the endpoint.</summary>
    /// <param name="endpoint">Validated user endpoint.</param>
    /// <param name="credentials">User credentials, when configured.</param>
    /// <param name="filePath">Local export file (engine-generated name).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the transfer finishes.</returns>
    Task SendAsync(DestinationEndpoint endpoint, DestinationCredentials? credentials, string filePath, CancellationToken cancellationToken);
}

/// <summary>
/// Raw transport probe result mapped to the locked
/// <see cref="Contracts.Api.DestinationTestResult"/> by the delivery service.
/// </summary>
/// <param name="IsReachable">Whether the endpoint answered.</param>
/// <param name="CredentialsValid">Whether credentials were accepted.</param>
/// <param name="Message">Verdict detail.</param>
public sealed record DestinationProbe(bool IsReachable, bool CredentialsValid, string? Message);

/// <summary>
/// Local-folder transport: copies the export into the user-configured folder.
/// The destination path is validated to stay inside that folder; the file
/// name is engine-generated, never server-controlled (safe path handling).
/// </summary>
public sealed class LocalFolderTransport : IExportTransport
{
    /// <inheritdoc/>
    public Task<DestinationProbe> TestAsync(DestinationEndpoint endpoint, DestinationCredentials? credentials, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(endpoint.Path!);
            return Task.FromResult(new DestinationProbe(true, true, "Local folder is writable."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new DestinationProbe(false, false, ex.Message));
        }
    }

    /// <inheritdoc/>
    public async Task SendAsync(DestinationEndpoint endpoint, DestinationCredentials? credentials, string filePath, CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(endpoint.Path!);
        Directory.CreateDirectory(directory);
        var target = Path.GetFullPath(Path.Combine(directory, Path.GetFileName(filePath)));
        if (!target.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !string.Equals(target, directory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The transfer target escapes the configured folder.");
        }

        await using var source = File.OpenRead(filePath);
        await using var destination = File.Create(target);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Webhook transport: POSTs the export file to the user-configured http(s)
/// endpoint with an optional user bearer token. The handler is injectable so
/// tests can record every request (criterion #14 zero-outbound proof).
/// </summary>
public sealed class WebhookTransport : IExportTransport, IDisposable
{
    private readonly HttpClient _client;

    /// <summary>Creates the transport with the default system handler.</summary>
    public WebhookTransport()
        : this(handler: null)
    {
    }

    /// <summary>Creates the transport over an explicit handler (test double).</summary>
    /// <param name="handler">HTTP handler, or null for the system handler.</param>
    public WebhookTransport(HttpMessageHandler? handler)
    {
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _client.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <inheritdoc/>
    public async Task<DestinationProbe> TestAsync(DestinationEndpoint endpoint, DestinationCredentials? credentials, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, endpoint.Url);
        ApplyToken(request, credentials);
        try
        {
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var credentialsValid = response.StatusCode is not (System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden);
            return new DestinationProbe(true, credentialsValid, $"Endpoint answered {(int)response.StatusCode}.");
        }
        catch (HttpRequestException ex)
        {
            return new DestinationProbe(false, false, ex.Message);
        }
    }

    /// <inheritdoc/>
    public async Task SendAsync(DestinationEndpoint endpoint, DestinationCredentials? credentials, string filePath, CancellationToken cancellationToken)
    {
        using var content = new StreamContent(File.OpenRead(filePath));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url) { Content = content };
        request.Headers.Add("X-CrawlForge-Export-File", Path.GetFileName(filePath));
        ApplyToken(request, credentials);
        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    public void Dispose() => _client.Dispose();

    private static void ApplyToken(HttpRequestMessage request, DestinationCredentials? credentials)
    {
        if (!string.IsNullOrEmpty(credentials?.Token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Token);
        }
    }
}

/// <summary>
/// FTP transport (FluentFTP): uploads the export to the user-configured FTP
/// server. The remote file name is the engine-generated export name appended
/// to the user-configured remote directory — never server-controlled.
/// </summary>
public sealed class FtpTransport : IExportTransport
{
    /// <inheritdoc/>
    public async Task<DestinationProbe> TestAsync(DestinationEndpoint endpoint, DestinationCredentials? credentials, CancellationToken cancellationToken)
    {
        var client = CreateClient(endpoint, credentials);
        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
            await client.GetListing(endpoint.RemotePath ?? "/", cancellationToken).ConfigureAwait(false);
            return new DestinationProbe(true, true, "FTP connect and listing succeeded.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DestinationProbe(false, false, ex.Message);
        }
        finally
        {
            await client.Disconnect(cancellationToken).ConfigureAwait(false);
            client.Dispose();
        }
    }

    /// <inheritdoc/>
    public async Task SendAsync(DestinationEndpoint endpoint, DestinationCredentials? credentials, string filePath, CancellationToken cancellationToken)
    {
        var client = CreateClient(endpoint, credentials);
        try
        {
            await client.Connect(cancellationToken).ConfigureAwait(false);
            var remote = CombineRemotePath(endpoint.RemotePath, Path.GetFileName(filePath));
            await client.UploadFile(filePath, remote, FtpRemoteExists.Overwrite, createRemoteDir: true, token: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await client.Disconnect(cancellationToken).ConfigureAwait(false);
            client.Dispose();
        }
    }

    private static AsyncFtpClient CreateClient(DestinationEndpoint endpoint, DestinationCredentials? credentials)
        => new(endpoint.Host, credentials?.Username ?? "anonymous", credentials?.Password ?? string.Empty, endpoint.Port ?? 21);

    internal static string CombineRemotePath(string? remotePath, string fileName)
    {
        var directory = (remotePath ?? string.Empty).TrimEnd('/');
        return directory.Length == 0 ? fileName : directory + "/" + fileName;
    }
}

/// <summary>
/// SFTP transport (SSH.NET 2026.0.0, patched for GHSA-mggc-4xg6-vcxf /
/// GHSA-q939-rpr3-3284): uploads the export to the user-configured SFTP
/// server. Only uploads are performed — no server-controlled name is ever
/// used for a local write; the remote path is the validated user directory
/// plus the engine-generated file name.
/// </summary>
public sealed class SftpTransport : IExportTransport
{
    /// <inheritdoc/>
    public async Task<DestinationProbe> TestAsync(DestinationEndpoint endpoint, DestinationCredentials? credentials, CancellationToken cancellationToken)
    {
        using var client = CreateClient(endpoint, credentials);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await foreach (var _ in client.ListDirectoryAsync(endpoint.RemotePath ?? ".", cancellationToken).ConfigureAwait(false))
            {
                break;
            }

            return new DestinationProbe(true, true, "SFTP connect and listing succeeded.");
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or IOException or System.Net.Sockets.SocketException)
        {
            return new DestinationProbe(false, false, ex.Message);
        }
        finally
        {
            client.Disconnect();
        }
    }

    /// <inheritdoc/>
    public async Task SendAsync(DestinationEndpoint endpoint, DestinationCredentials? credentials, string filePath, CancellationToken cancellationToken)
    {
        using var client = CreateClient(endpoint, credentials);
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var remote = FtpTransport.CombineRemotePath(endpoint.RemotePath, Path.GetFileName(filePath));
            await using var stream = File.OpenRead(filePath);
            await client.UploadFileAsync(stream, remote, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            client.Disconnect();
        }
    }

    private static SftpClient CreateClient(DestinationEndpoint endpoint, DestinationCredentials? credentials)
        => new(endpoint.Host!, endpoint.Port ?? 22, credentials?.Username ?? string.Empty, credentials?.Password ?? string.Empty);
}
