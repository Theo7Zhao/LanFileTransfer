using System.IO;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Logging;

namespace TheoTransfer.Core;

public sealed record PairRequest(string? Code, string? Key);

public sealed class TransferServer : IAsyncDisposable
{
    private static readonly string WebUi = LoadWebUi();
    private readonly AppCore _core;
    private WebApplication? _app;
    private Timer? _cleanupTimer;

    public event Action<TransferRecord>? RecordAdded;

    public TransferServer(AppCore core) => _core = core;

    public bool IsRunning => _app != null;

    public async Task StartAsync(int port)
    {
        if (_app != null) throw new InvalidOperationException("服务已在运行，请先停止");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "TheoTransfer",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(IPAddress.Any, port);
            o.Limits.MaxRequestBodySize = null;
            o.Limits.MaxConcurrentConnections = 32;
            o.AddServerHeader = false;
        });

        var app = builder.Build();
        MapRoutes(app);
        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
        _app = app;
        _cleanupTimer?.Dispose();
        _cleanupTimer = new Timer(_ => _core.CleanupExpired(), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public async Task StopAsync()
    {
        _cleanupTimer?.Dispose();
        _cleanupTimer = null;
        if (_app != null)
        {
            var app = _app;
            _app = null;
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private void MapRoutes(WebApplication app)
    {
        var core = _core;

        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            if (path.StartsWithSegments("/api") &&
                !path.StartsWithSegments("/api/pair") &&
                !path.StartsWithSegments("/api/info"))
            {
                var token = ctx.Request.Headers["X-Auth-Token"].FirstOrDefault()
                            ?? ctx.Request.Query["token"].FirstOrDefault();
                if (!core.IsValidToken(token))
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                    return;
                }
            }
            await next();
        });

        app.MapGet("/", (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            return ctx.Response.WriteAsync(WebUi);
        });

        app.MapGet("/api/info", () => Results.Json(new { name = Environment.MachineName }));

        app.MapGet("/api/verify", () => Results.Json(new { ok = true }));

        app.MapPost("/api/pair", async (HttpContext ctx) =>
        {
            PairRequest? req;
            try { req = await ctx.Request.ReadFromJsonAsync<PairRequest>(); }
            catch { req = null; }
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "?";
            var token = core.TryPair(ip, req?.Code ?? "", req?.Key);
            return token == null
                ? Results.Json(new { error = "denied" }, statusCode: core.IsPairLocked(ip) ? 429 : 401)
                : Results.Json(new { token });
        });

        app.MapPost("/api/upload/init", (HttpContext ctx) =>
        {
            var name = CleanName(ctx.Request.Query["name"].FirstOrDefault());
            if (!long.TryParse(ctx.Request.Query["size"], out var size) || size <= 0)
                return Results.Json(new { error = "bad size" }, statusCode: 400);

            var session = core.CreateUpload(name, size);
            var record = new TransferRecord
            {
                Direction = TransferDirection.PhoneToPc,
                FileName = name,
                TotalBytes = size,
            };
            session.Record = record;
            RecordAdded?.Invoke(record);
            return Results.Json(new { uploadId = session.Id, offset = session.Received, chunkSize = AppCore.ChunkSize });
        });

        app.MapPut("/api/upload/chunk", async (HttpContext ctx) =>
        {
            var id = ctx.Request.Query["id"].FirstOrDefault();
            if (id == null || !core.Uploads.TryGetValue(id, out var s))
                return Results.Json(new { error = "no session" }, statusCode: 404);
            if (!long.TryParse(ctx.Request.Query["offset"], out var offset))
                return Results.Json(new { error = "bad offset" }, statusCode: 400);

            await s.Sem.WaitAsync(ctx.RequestAborted);
            try
            {
                if (s.Completed)
                    return Results.Json(new { error = "completed" }, statusCode: 400);
                if (offset != s.Received)
                    return Results.Json(new { expected = s.Received }, statusCode: 409);

                var len = ctx.Request.ContentLength ?? -1;
                if (len <= 0 || offset + len > s.TotalSize)
                    return Results.Json(new { error = "bad length" }, statusCode: 400);

                s.Record?.MarkTransferring();
                s.LastActive = DateTime.UtcNow;

                var stream = s.Stream!;
                stream.SetLength(offset);
                stream.Position = offset;
                await ctx.Request.Body.CopyToAsync(stream, 1024 * 1024, ctx.RequestAborted);
                if (stream.Length != offset + len)
                    return Results.Json(new { error = "short body" }, statusCode: 400);

                s.Received = offset + len;
                s.Record?.SetBytes(s.Received);
                return Results.Json(new { received = s.Received, total = s.TotalSize });
            }
            catch (OperationCanceledException)
            {
                return Results.Json(new { error = "cancelled" }, statusCode: 400);
            }
            finally
            {
                s.Sem.Release();
            }
        });

        app.MapPost("/api/upload/complete", (HttpContext ctx) =>
        {
            var id = ctx.Request.Query["id"].FirstOrDefault();
            if (id == null || !core.Uploads.TryGetValue(id, out var s))
                return Results.Json(new { error = "no session" }, statusCode: 404);

            s.Sem.Wait();
            try
            {
                if (s.Completed)
                    return Results.Json(new { error = "completed" }, statusCode: 400);
                if (s.Received != s.TotalSize)
                    return Results.Json(
                        new { expected = s.Received, total = s.TotalSize }, statusCode: 409);

                s.Completed = true;
                s.Stream?.Dispose();
                s.Stream = null;
                var final = core.CompleteUpload(s);
                s.Record?.MarkDone(final);
                return Results.Json(new { fileName = Path.GetFileName(final), size = s.TotalSize });
            }
            finally
            {
                s.Sem.Release();
            }
        });

        app.MapPost("/api/upload/cancel", (HttpContext ctx) =>
        {
            var id = ctx.Request.Query["id"].FirstOrDefault();
            if (id != null) core.CancelUpload(id, "手机端取消");
            return Results.Ok();
        });

        app.MapGet("/api/outbox", () => Results.Json(new
        {
            files = core.Outbox.Values
                .OrderByDescending(f => f.AddedAt)
                .Select(f => new { id = f.Id, name = f.Name, size = f.Size }),
        }));

        app.MapGet("/api/outbox/{id:guid}", DownloadSharedAsync);
    }

    private async Task DownloadSharedAsync(HttpContext ctx)
    {
        if (!_core.Outbox.TryGetValue(ctx.Request.RouteValues["id"]?.ToString() ?? "", out var f))
        {
            ctx.Response.StatusCode = 404;
            return;
        }

        var inline = ctx.Request.Query["inline"].Count > 0;
        var rangeHeader = ctx.Request.Headers.Range.FirstOrDefault();
        // 覆盖整个文件的 Range（如 bytes=0-）按完整下载处理；只有真正的部分请求才走 206
        var ranged = TryParseRange(rangeHeader, f.Size, out var start, out var end)
                     && !(start == 0 && end == f.Size - 1);

        ctx.Response.Headers.AcceptRanges = "bytes";
        ctx.Response.Headers.ContentDisposition = ContentDisposition(f.Name, inline);
        ctx.Response.ContentType = inline ? MimeFor(f.Name) : "application/octet-stream";
        ctx.Response.ContentLength = end - start + 1;

        TransferRecord? record = null;
        if (!ranged)
        {
            record = new TransferRecord
            {
                Direction = TransferDirection.PcToPhone,
                FileName = f.Name,
                TotalBytes = f.Size,
            };
            record.MarkTransferring();
            RecordAdded?.Invoke(record);
            ctx.Response.StatusCode = 200;
        }
        else
        {
            ctx.Response.StatusCode = 206;
            ctx.Response.Headers.ContentRange = $"bytes {start}-{end}/{f.Size}";
        }

        try
        {
            await using var fs = File.OpenRead(f.Path);
            fs.Position = start;
            var remaining = end - start + 1;
            var buf = new byte[1024 * 1024];
            while (remaining > 0)
            {
                var n = await fs.ReadAsync(buf, 0, (int)Math.Min(buf.Length, remaining), ctx.RequestAborted);
                if (n <= 0) break;
                await ctx.Response.Body.WriteAsync(buf, 0, n, ctx.RequestAborted);
                remaining -= n;
                record?.AddBytes(n);
            }
            if (record != null)
            {
                if (remaining == 0) record.MarkDone();
                else record.MarkFailed("手机端中断");
            }
        }
        catch (OperationCanceledException)
        {
            record?.MarkFailed("手机端中断");
        }
        catch (Exception)
        {
            record?.MarkFailed("传输错误");
        }
    }

    private static bool TryParseRange(string? header, long size, out long start, out long end)
    {
        start = 0;
        end = size - 1;
        if (string.IsNullOrEmpty(header)) return false;
        var parts = header.Split('=', 2);
        if (parts.Length != 2 || !parts[0].Trim().Equals("bytes", StringComparison.OrdinalIgnoreCase))
            return false;
        var range = parts[1].Split(',')[0].Trim();
        var dash = range.IndexOf('-');
        if (dash < 0) return false;
        var a = range[..dash].Trim();
        var b = range[(dash + 1)..].Trim();
        if (a.Length == 0)
        {
            if (!long.TryParse(b, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, size - suffix);
            end = size - 1;
            return true;
        }
        if (!long.TryParse(a, out var s) || s < 0 || s >= size) return false;
        start = s;
        if (b.Length == 0) { end = size - 1; return true; }
        if (!long.TryParse(b, out var e) || e < s) return false;
        end = Math.Min(e, size - 1);
        return true;
    }

    private static string ContentDisposition(string name, bool inline)
    {
        var type = inline ? "inline" : "attachment";
        return $"{type}; filename=\"download\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
    }

    private static string MimeFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mov" => "video/quicktime",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".pdf" => "application/pdf",
        ".txt" => "text/plain; charset=utf-8",
        _ => "application/octet-stream",
    };

    private static string CleanName(string? raw)
    {
        var name = Path.GetFileName((raw ?? "file").Replace('\0', ' ')).Trim();
        if (name.Length == 0) name = "file";
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name.Length > 180) name = name[^180..];
        return name;
    }

    private static string LoadWebUi()
    {
        var asm = typeof(TransferServer).Assembly;
        using var stream = asm.GetManifestResourceStream("TheoTransfer.Core.WebUI.html")
            ?? throw new InvalidOperationException("缺少内嵌资源 WebUI.html");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
