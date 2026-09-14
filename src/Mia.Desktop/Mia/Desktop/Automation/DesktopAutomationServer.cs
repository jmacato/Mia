// SPDX-License-Identifier: MIT

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Mia.Desktop.Automation;

/// <summary>
/// Loopback-only automation for driving the desktop viewer through its normal
/// session APIs. It never writes firmware state directly.
/// </summary>
internal sealed class DesktopAutomationServer : IAsyncDisposable
{
    readonly int _port;
    readonly EmulatorSession _session;
    CancellationTokenSource? _cancellation;
    HttpListener? _listener;
    Task? _serveTask;

    public DesktopAutomationServer(int port, EmulatorSession session)
    {
        _port = port;
        _session = session;
    }

    public async Task StartAsync()
    {
        if (_port == 0 || _listener is not null)
        {
            return;
        }

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        listener.Start();
        _listener = listener;
        _cancellation = new CancellationTokenSource();
        _serveTask = Task.Run(() => ServeAsync(listener, _cancellation.Token));
        await Task.Yield();
    }

    public async ValueTask DisposeAsync()
    {
        var serveTask = Interlocked.Exchange(ref _serveTask, null);
        CancellationTokenSource? cancellation = _cancellation;
        await CancelAsync(cancellation).ConfigureAwait(false);
        _cancellation = null;
        CloseListener();
        await ObserveShutdownAsync(serveTask).ConfigureAwait(false);
    }

    static async Task CancelAsync(CancellationTokenSource? cancellation)
    {
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            cancellation.Dispose();
        }
    }

    void CloseListener()
    {
        if (_listener is not null)
        {
            _listener.Close();
            _listener = null;
        }
    }

    static async Task ObserveShutdownAsync(Task? serveTask)
    {
        if (serveTask is null)
        {
            return;
        }

        try
        {
            await serveTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    async Task ServeAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(context), CancellationToken.None);
        }
    }

    async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            await RouteAsync(context).ConfigureAwait(false);
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.BadRequest, error.Message).ConfigureAwait(false);
        }
        catch (Exception error) when (error is HttpListenerException or IOException or
            ObjectDisposedException or InvalidOperationException)
        {
            await WriteErrorAsync(context.Response, HttpStatusCode.InternalServerError, error.Message).ConfigureAwait(false);
        }
    }

    Task RouteAsync(HttpListenerContext context) =>
        (context.Request.HttpMethod, context.Request.Url?.AbsolutePath) switch
        {
            ("GET", "/api/v1/status") => WriteStatusAsync(context.Response),
            ("GET", "/api/v1/frame.rgb332") => WriteFrameAsync(context.Response),
            ("POST", "/api/v1/gsm/incoming-sms") =>
                QueueIncomingSmsAsync(context),
            ("POST", "/api/v1/gsm/incoming-call") =>
                QueueIncomingCallAsync(context),
            ("POST", "/api/v1/gsm/rssi") => SetRssiAsync(context),
            ("POST", "/api/v1/key/yes") =>
                PressKeyAsync(context.Response, 0x0d, 0x01),
            ("POST", "/api/v1/key/center") =>
                PressKeyAsync(context.Response, 0x0f, 0x08),
            ("POST", "/api/v1/key/no") => PressNoKeyAsync(context.Response),
            _ => WriteErrorAsync(
                context.Response,
                HttpStatusCode.NotFound,
                "Unknown endpoint."),
        };

    Task WriteStatusAsync(HttpListenerResponse response) =>
        WriteJsonAsync(
            response,
            HttpStatusCode.OK,
            _session.GetGsmStatus(),
            DesktopAutomationServerJsonContext.Default.MiaLiveGsmStatus);

    async Task WriteFrameAsync(HttpListenerResponse response)
    {
        var frame = _session.GetFrameSnapshot();
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/octet-stream";
        response.ContentLength64 = frame.Length;
        await response.OutputStream.WriteAsync(frame).ConfigureAwait(false);
        response.Close();
    }

    async Task QueueIncomingSmsAsync(HttpListenerContext context)
    {
        var request = await JsonSerializer.DeserializeAsync(
            context.Request.InputStream,
            DesktopAutomationServerJsonContext.Default.DesktopAutomationServerIncomingSmsRequest).ConfigureAwait(false);
        if (request is null)
        {
            await WriteMissingBodyAsync(context.Response).ConfigureAwait(false);
            return;
        }

        var accepted = _session.TryQueueIncomingSms(
            request.Originator ?? string.Empty,
            request.Text ?? string.Empty,
            out var result);
        await WriteGsmCommandResponseAsync(
            context.Response,
            accepted,
            result,
            accepted ? HttpStatusCode.Accepted : HttpStatusCode.Conflict).ConfigureAwait(false);
    }

    async Task QueueIncomingCallAsync(HttpListenerContext context)
    {
        var request = await JsonSerializer.DeserializeAsync(
            context.Request.InputStream,
            DesktopAutomationServerJsonContext.Default.DesktopAutomationServerIncomingCallRequest).ConfigureAwait(false);
        if (request is null)
        {
            await WriteMissingBodyAsync(context.Response).ConfigureAwait(false);
            return;
        }

        var accepted = _session.TryQueueIncomingCall(
            request.Originator ?? string.Empty,
            request.AutoAnswer,
            out var result);
        await WriteGsmCommandResponseAsync(
            context.Response,
            accepted,
            result,
            accepted ? HttpStatusCode.Accepted : HttpStatusCode.Conflict).ConfigureAwait(false);
    }

    async Task SetRssiAsync(HttpListenerContext context)
    {
        var request = await JsonSerializer.DeserializeAsync(
            context.Request.InputStream,
            DesktopAutomationServerJsonContext.Default.DesktopAutomationServerRssiRequest).ConfigureAwait(false);
        if (request is null)
        {
            await WriteMissingBodyAsync(context.Response).ConfigureAwait(false);
            return;
        }

        var accepted = _session.TrySetCarrierRawSample(
            request.RawSample,
            out var result);
        await WriteGsmCommandResponseAsync(
            context.Response,
            accepted,
            result,
            accepted ? HttpStatusCode.OK : HttpStatusCode.BadRequest).ConfigureAwait(false);
    }

    async Task PressKeyAsync(
        HttpListenerResponse response,
        byte scanMask,
        byte rowMask)
    {
        var accepted = await _session.PressKeyAsync(scanMask, rowMask)
            .ConfigureAwait(false);
        await WriteKeyCommandResponseAsync(response, accepted).ConfigureAwait(false);
    }

    async Task PressNoKeyAsync(HttpListenerResponse response)
    {
        var accepted = await _session.PressNoKeyAsync().ConfigureAwait(false);
        await WriteKeyCommandResponseAsync(response, accepted).ConfigureAwait(false);
    }

    Task WriteGsmCommandResponseAsync(
        HttpListenerResponse response,
        bool accepted,
        string result,
        HttpStatusCode statusCode) =>
        WriteJsonAsync(
            response,
            statusCode,
            new DesktopAutomationServerGsmCommandResponse(
                accepted,
                result,
                _session.GetGsmStatus()),
            DesktopAutomationServerJsonContext.Default.DesktopAutomationServerGsmCommandResponse);

    Task WriteKeyCommandResponseAsync(
        HttpListenerResponse response,
        bool accepted) =>
        WriteJsonAsync(
            response,
            accepted ? HttpStatusCode.Accepted : HttpStatusCode.Conflict,
            new DesktopAutomationServerKeyCommandResponse(
                accepted,
                _session.GetGsmStatus()),
            DesktopAutomationServerJsonContext.Default.DesktopAutomationServerKeyCommandResponse);

    static Task WriteMissingBodyAsync(HttpListenerResponse response) =>
        WriteErrorAsync(
            response,
            HttpStatusCode.BadRequest,
            "Expected JSON body.");

    static Task WriteErrorAsync(HttpListenerResponse response, HttpStatusCode statusCode, string message) =>
        WriteJsonAsync(
            response,
            statusCode,
            new DesktopAutomationServerErrorResponse(message),
            DesktopAutomationServerJsonContext.Default.DesktopAutomationServerErrorResponse);

    static async Task WriteJsonAsync<T>(
        HttpListenerResponse response,
        HttpStatusCode statusCode,
        T body,
        JsonTypeInfo<T> typeInfo)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.OutputStream, body, typeInfo).ConfigureAwait(false);
        response.Close();
    }
}
