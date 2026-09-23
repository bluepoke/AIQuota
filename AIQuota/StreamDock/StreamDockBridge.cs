using System.Net;
using System.Text;
using System.Text.Json;

namespace AIQuota.StreamDock;

/// <summary>
/// Minimal localhost-only HTTP bridge so a Stream Dock plugin process (launched by
/// Mirabox/Soomfon's Stream Dock host app - see
/// https://github.com/MiraboxSpace/StreamDock-Plugin-SDK) can mirror the tray icon's usage
/// picture onto a physical deck key and trigger a refresh from a button press, without
/// AIQuota having to speak the deck's own WebSocket plugin protocol directly.
///
/// Bound to 127.0.0.1 only (never reachable from the network). Every request must also
/// carry the <see cref="ClientHeaderName"/> header: not a secret, but a browser tab can't
/// attach a custom header without triggering a CORS preflight, and this server never
/// answers OPTIONS with permissive CORS headers - so a malicious webpage's fetch() can't
/// reach these endpoints even though nothing here is otherwise authenticated. This keeps
/// out casual/drive-by requests, not a determined local attacker (who could already do
/// anything the logged-in user can do).
/// </summary>
public sealed class StreamDockBridge : IDisposable
{
    /// <summary>Fixed by convention with the companion Python plugin (streamdock-plugin/) -
    /// not user-configurable, since both sides would need to agree on it.</summary>
    public const int Port = 51477;

    private const string ClientHeaderName = "X-AIQuota-Client";

    private readonly HttpListener _listener = new();
    private readonly Func<Task> _requestRefresh;
    private readonly SynchronizationContext _uiContext;
    private CancellationTokenSource? _cts;

    public StreamDockBridge(Func<Task> requestRefresh, SynchronizationContext uiContext)
    {
        _requestRefresh = requestRefresh;
        _uiContext = uiContext;
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public bool IsRunning => _cts is not null;

    public void Start()
    {
        if (IsRunning)
            return;
        _cts = new CancellationTokenSource();
        _listener.Start();
        _ = AcceptLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        if (_cts is not { } cts)
            return;
        _cts = null;
        cts.Cancel();
        _listener.Stop();
        cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.Headers[ClientHeaderName] is null)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                context.Response.Close();
                return;
            }

            if (context.Request.HttpMethod == "GET" && context.Request.Url?.AbsolutePath == "/status")
            {
                await WriteStatusAsync(context);
            }
            else if (context.Request.HttpMethod == "POST" && context.Request.Url?.AbsolutePath == "/refresh")
            {
                _uiContext.Post(_ => _ = _requestRefresh(), null);
                context.Response.StatusCode = (int)HttpStatusCode.Accepted;
                context.Response.Close();
            }
            else
            {
                context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                context.Response.Close();
            }
        }
        catch (Exception)
        {
            try { context.Response.Abort(); } catch (Exception) { /* response already closed */ }
        }
    }

    private static async Task WriteStatusAsync(HttpListenerContext context)
    {
        var snapshot = DeckStatusHolder.Current;
        var imageBytes = DeckKeyRenderer.Render(snapshot);
        var json = JsonSerializer.Serialize(new
        {
            loggedIn = snapshot.LoggedIn,
            sessionPercent = snapshot.SessionPercent,
            weeklyPercent = snapshot.WeeklyPercent,
            creditPercent = snapshot.CreditPercent,
            image = $"data:image/png;base64,{Convert.ToBase64String(imageBytes)}",
        });

        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    public void Dispose()
    {
        Stop();
        ((IDisposable)_listener).Dispose();
    }
}
