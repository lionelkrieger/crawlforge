using System.Net;
using System.Text;

namespace CrawlForge.Boundary.Tests.Harness;

/// <summary>
/// Loopback webhook receiver standing in for a USER-configured destination
/// endpoint: records method, path, authorization header, and body of every
/// delivery so tests can prove transfers go only to the user's endpoint with
/// the user's credentials.
/// </summary>
public sealed class WebhookReceiver : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly List<ReceivedDelivery> _deliveries = [];
    private readonly object _gate = new();

    private WebhookReceiver(int port)
    {
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(() => ServeAsync(_stop.Token));
    }

    /// <summary>Loopback base URL of the receiver.</summary>
    public string BaseUrl { get; }

    /// <summary>Recorded deliveries.</summary>
    public IReadOnlyList<ReceivedDelivery> Deliveries
    {
        get { lock (_gate) { return [.. _deliveries]; } }
    }

    /// <summary>Starts the receiver on a free loopback port.</summary>
    /// <returns>The running receiver.</returns>
    public static WebhookReceiver Start()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new WebhookReceiver(port);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }

        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or HttpListenerException)
        {
            // Shutdown races.
        }

        _stop.Dispose();
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync(cancellationToken);
            }

            lock (_gate)
            {
                _deliveries.Add(new ReceivedDelivery(
                    context.Request.HttpMethod,
                    context.Request.Url?.AbsolutePath ?? "/",
                    context.Request.Headers["Authorization"],
                    body));
            }

            context.Response.StatusCode = 200;
            context.Response.Close();
        }
    }
}

/// <summary>One recorded delivery to the user endpoint.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="Path">Request path.</param>
/// <param name="Authorization">Authorization header value (user credential).</param>
/// <param name="Body">Request body.</param>
public sealed record ReceivedDelivery(string Method, string Path, string? Authorization, string Body);
