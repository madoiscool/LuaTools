using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace GameGuy.Services;

/// <summary>
/// The one <see cref="HttpClient"/> factory for the whole app, so every outbound request shares a
/// single handler and therefore a single point of control over how host names get resolved.
/// </summary>
/// <remarks>
/// <para>
/// Why a static rather than a DI singleton: the thirteen clients it replaces are FIELD INITIALIZERS
/// (<c>private readonly HttpClient _http = new() { … }</c>), so they are constructed while the DI
/// container is still resolving services. Anything read at construction time would depend on service
/// ordering. <see cref="Settings"/> is therefore read lazily inside the connect callback instead, which
/// runs per-connection, long after startup has settled.
/// </para>
/// <para>
/// Sharing one handler is also just correct: pooled connections are reused across services instead of
/// each one holding its own pool.
/// </para>
/// </remarks>
public static class AppHttp
{
    /// <summary>
    /// Supplies the current mode. Set once at startup; invoked per connection, never cached.
    /// </summary>
    /// <remarks>
    /// A delegate rather than a <see cref="SettingsService"/> reference so the handler has no dependency
    /// on settings storage at all — it only needs to know a mode, and it asks at the moment it matters.
    /// </remarks>
    public static Func<string>? ModeProvider { get; set; }

    private static readonly DohResolver Resolver = new();

    /// <summary>
    /// Auto mode's latch: set only once DoH has actually succeeded where the system resolver failed.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT set on any connect failure. A server being down, a dropped wifi link or a
    /// timeout would otherwise flip the whole app onto DoH for the rest of the session on the strength
    /// of one unrelated blip. Requiring DoH to succeed where the system failed is the only evidence
    /// that actually distinguishes "DNS is being tampered with" from "the network hiccuped".
    /// </remarks>
    private static volatile bool _systemDnsSuspect;

    private static readonly SocketsHttpHandler Handler = new()
    {
        // Bounds how long a mode change takes to matter: pooled connections outlive the setting, so
        // without this a user who switches to Always keeps using system-resolved sockets until the
        // server closes them.
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = ConnectAsync,
    };

    /// <summary>A client on the shared handler. The handler is never disposed with the client.</summary>
    public static HttpClient Create(TimeSpan timeout, Uri? baseAddress = null) =>
        new(Handler, disposeHandler: false) { Timeout = timeout, BaseAddress = baseAddress };

    /// <summary>The shared handler, for the few callers that need a handler rather than a client.</summary>
    public static HttpMessageHandler SharedHandler => Handler;

    /// <summary>Current mode: "Auto" (default), "Always" or "Never".</summary>
    private static string Mode
    {
        get
        {
            try { return ModeProvider?.Invoke() ?? "Auto"; }
            catch { return "Auto"; } // a broken provider must not take down every request
        }
    }

    /// <summary>
    /// Open the TCP connection for one request, choosing how the name is resolved.
    /// </summary>
    /// <remarks>
    /// Returns the RAW transport stream, before TLS. The handler performs the handshake itself against
    /// the request's original host name, so SNI and certificate validation are untouched no matter
    /// which resolver produced the address. That is the whole reason this is a connect callback rather
    /// than URL rewriting to an IP, which would break both.
    /// </remarks>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        string host = ctx.DnsEndPoint.Host;
        int port = ctx.DnsEndPoint.Port;
        string mode = Mode;

        if (mode == "Never" || DohResolver.ShouldBypass(host))
            return await ConnectBySystem(host, port, ct);

        if (mode == "Always")
        {
            var addresses = await Resolver.ResolveAsync(host, ct);
            if (addresses.Length > 0)
            {
                try { return await ConnectTo(addresses, port, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* DoH gave us addresses that don't work; the system may know better */ }
            }
            return await ConnectBySystem(host, port, ct);
        }

        // ── Auto ──────────────────────────────────────────────────────────────────────────────────
        // Once DoH has proven itself this session, skip the attempt we already know will fail.
        if (_systemDnsSuspect)
        {
            var addresses = await Resolver.ResolveAsync(host, ct);
            if (addresses.Length > 0) return await ConnectTo(addresses, port, ct);
            return await ConnectBySystem(host, port, ct);
        }

        try
        {
            return await ConnectBySystem(host, port, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception systemFailure)
        {
            // This catches the case that actually matters. ISP DNS blocking usually answers with a
            // SINKHOLE address rather than NXDOMAIN, so the lookup "succeeds" and it is the connect
            // that fails. Escalating on connect failure — not just on resolution failure — is what
            // makes Auto work against real-world blocking.
            var addresses = await Resolver.ResolveAsync(host, ct);
            if (addresses.Length == 0) throw;

            Stream stream;
            try { stream = await ConnectTo(addresses, port, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { throw systemFailure; } // DoH was no better: report the original, it is the real one
            _systemDnsSuspect = true;
            return stream;
        }
    }

    /// <summary>Let the socket do the name resolution, i.e. the system resolver.</summary>
    private static async ValueTask<Stream> ConnectBySystem(string host, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    /// <summary>Connect to already-resolved addresses, trying each in turn.</summary>
    private static async ValueTask<Stream> ConnectTo(IPAddress[] addresses, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}
