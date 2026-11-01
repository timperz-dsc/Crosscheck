using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Crosscheck.Bridge.Api;

/// <summary>
/// Local API for the coming UI (CRO-20). Bound to 127.0.0.1 only; requests from web pages of
/// other origins are refused. GET /status, WebSocket /events.
/// </summary>
public sealed class LocalApi : IAsyncDisposable
{
    private readonly WebApplication _app;

    private LocalApi(WebApplication app, int port)
    {
        _app = app;
        Port = port;
    }

    public int Port { get; }

    public static async Task<LocalApi> StartAsync(int port, Func<string> status, EventHub hub)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        app.UseWebSockets();
        app.Use(async (ctx, next) =>
        {
            if (!LocalOrigin(ctx.Request))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            await next();
        });
        app.MapGet("/status", () => Results.Content(status(), "application/json"));
        app.Map("/events", async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await Stream(ws, hub, ctx.RequestAborted);
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new LocalApi(app, new Uri(address).Port);
    }

    private static bool LocalOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host == "localhost");
    }

    private static async Task Stream(WebSocket ws, EventHub hub, CancellationToken ct)
    {
        var queue = Channel.CreateBounded<BridgeEvent>(new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });
        void OnEvent(BridgeEvent e) => queue.Writer.TryWrite(e);
        hub.Published += OnEvent;
        try
        {
            var reader = Task.Run(async () =>
            {
                var buffer = new byte[1024];
                while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
                {
                    var r = await ws.ReceiveAsync(buffer, ct);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                }
                queue.Writer.TryComplete();
            }, ct);
            await foreach (var e in queue.Reader.ReadAllAsync(ct))
                await ws.SendAsync(Encoding.UTF8.GetBytes(e.ToJson().ToJsonString()), WebSocketMessageType.Text, true, ct);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException)
        {
        }
        finally
        {
            hub.Published -= OnEvent;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
