using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Crosscheck.Bridge.Api;

/// <summary>
/// Local API and UI. Bound to 127.0.0.1 only. Refuses requests from web pages of other
/// origins, requests with a foreign Host header (DNS rebinding), and POSTs that are not JSON
/// (so another site cannot send them without a CORS preflight, which is never granted).
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

    public static async Task<LocalApi> StartAsync(int port, IBridgeControl bridge, EventHub hub, string uiDir)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
        var app = builder.Build();
        app.UseWebSockets();
        app.Use(async (ctx, next) =>
        {
            if (!LocalOrigin(ctx.Request) || !LocalHost(ctx.Request))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
            {
                ctx.Response.StatusCode = StatusCodes.Status415UnsupportedMediaType;
                return;
            }
            await next();
        });

        app.MapGet("/api/status", () => Results.Content(bridge.StatusJson().ToJsonString(), "application/json"));
        app.MapGet("/api/recent", () => Results.Content(bridge.Recent().ToJsonString(), "application/json"));
        app.Map("/api/events", async ctx =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await Stream(ws, hub, ctx.RequestAborted);
        });

        app.MapPost("/api/settings/server", async (HttpRequest req) =>
            await Body(req) is { } b && b["url"]?.GetValue<string>() is { } url ? Answer(bridge.SetServer(url)) : BadRequest("url missing"));
        app.MapPost("/api/session/create", async () => Answer(await bridge.CreateAsync()));
        app.MapPost("/api/session/check", async (HttpRequest req) =>
            await Body(req) is { } b && b["code"]?.GetValue<string>() is { } code ? Answer(await bridge.CheckAsync(code)) : BadRequest("code missing"));
        app.MapPost("/api/session/join", async (HttpRequest req) =>
            await Body(req) is { } b && b["code"]?.GetValue<string>() is { } code ? Answer(await bridge.JoinAsync(code)) : BadRequest("code missing"));
        app.MapPost("/api/session/cancel", () => Answer(bridge.CancelPreflight()));
        app.MapPost("/api/session/leave", async () => Answer(await bridge.LeaveAsync()));
        app.MapPost("/api/control/take", async (HttpRequest req) =>
            Answer(bridge.TakeControl((await Body(req))?["confirm_peer_lost"]?.GetValue<bool>() == true)));
        app.MapPost("/api/control/give", () => Answer(bridge.GiveControl()));
        app.MapPost("/api/update/apply", () => Answer(bridge.ApplyUpdate()));
        app.MapPost("/api/set", async (HttpRequest req) =>
        {
            var b = await Body(req);
            if (b?["var"]?.GetValue<string>() is not { } name || b["value"] is not JsonValue v || !v.TryGetValue<double>(out var value))
                return BadRequest("var and value needed");
            return Answer(await bridge.PilotSetAsync(name, value));
        });
        app.MapPost("/api/press-here", async (HttpRequest req) =>
            await Body(req) is { } b && b["var"]?.GetValue<string>() is { } name ? Answer(await bridge.PressHereOnlyAsync(name)) : BadRequest("var needed"));

        if (File.Exists(Path.Combine(uiDir, "index.html")))
        {
            var files = new PhysicalFileProvider(Path.GetFullPath(uiDir));
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
            app.MapFallback(ctx => ctx.Request.Path.StartsWithSegments("/api")
                ? Task.FromResult(ctx.Response.StatusCode = StatusCodes.Status404NotFound)
                : ctx.Response.SendFileAsync(files.GetFileInfo("index.html")));
        }
        else
        {
            app.MapGet("/", () => Results.Content(
                "<!doctype html><meta charset=utf-8><title>Crosscheck</title><p>The Crosscheck UI is not built. Run <code>npm run build</code> in ui/, then build the bridge again.</p>",
                "text/html"));
        }

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new LocalApi(app, new Uri(address).Port);
    }

    private static IResult Answer(ApiResult r) =>
        r.Ok ? Results.Json(new { ok = true }) : Results.Json(new { ok = false, error = r.Error }, statusCode: StatusCodes.Status409Conflict);

    private static IResult BadRequest(string error) => Results.Json(new { ok = false, error }, statusCode: StatusCodes.Status400BadRequest);

    private static async Task<JsonObject?> Body(HttpRequest req)
    {
        try
        {
            return await JsonNode.ParseAsync(req.Body) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static bool LocalOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true;
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && (uri.IsLoopback || uri.Host == "localhost");
    }

    private static bool LocalHost(HttpRequest request) => request.Host.Host is "127.0.0.1" or "localhost";

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
