using DeskDx;

namespace DeskDx.Mcp;

/// <summary>
/// Owns the <see cref="AppSession"/> instances of the MCP server, one per <c>sessionId</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One application instance per sessionId.</b> Parallel work means several server instances, not
/// several apps inside one — the spec's deliberate scope. That keeps the lifetime rule simple: the
/// session, its process and its trace directory belong together.
/// </para>
/// <para>
/// <b>Two cleanup paths.</b> The client disconnect ends the process, the host disposes this registry
/// and every session with it; a session that is simply left alone is swept by the idle timer. Both
/// are needed: an agent that crashes or is restarted never sends a "dispose" call.
/// </para>
/// <para>
/// <b>Per-session gate.</b> The engine is not thread-safe per session. The stdio transport delivers
/// one request at a time, but a host that pipelines (or a future multi-request transport) must not
/// interleave UIA calls on one session, so every operation runs under
/// <see cref="SessionHandle.Gate"/>.
/// </para>
/// </remarks>
public sealed class SessionRegistry : IDisposable
{
    /// <summary>How long a session may stay untouched before it is disposed. Default 10 minutes.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Dictionary<string, SessionHandle> _sessions = new(StringComparer.Ordinal);
    private readonly TimeSpan _idleTimeout;
    private readonly Timer _sweeper;

    private int _counter;
    private bool _disposed;

    /// <summary>Creates a registry and starts its idle sweeper.</summary>
    /// <param name="idleTimeout">Idle timeout. Defaults to <see cref="DefaultIdleTimeout"/>.</param>
    /// <param name="sweepInterval">How often idle sessions are checked.</param>
    public SessionRegistry(TimeSpan? idleTimeout = null, TimeSpan? sweepInterval = null)
    {
        _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
        var interval = sweepInterval ?? DefaultSweepInterval;

        // The timer keeps only a weak reason to exist: it exists so a forgotten session does not keep
        // a desktop application running forever.
        _sweeper = new Timer(_ => Sweep(), state: null, interval, interval);
    }

    /// <summary>Number of live sessions.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>Registers a session and returns its generated <c>sessionId</c>.</summary>
    public string Register(AppSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            var id = "s" + (++_counter).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _sessions[id] = new SessionHandle(id, session);
            return id;
        }
    }

    /// <summary>Resolves a session and marks it as used.</summary>
    public bool TryGet(string sessionId, out SessionHandle handle)
    {
        lock (_gate)
        {
            if (!string.IsNullOrEmpty(sessionId) && _sessions.TryGetValue(sessionId, out var found))
            {
                found.Touch();
                handle = found;
                return true;
            }

            handle = null!;
            return false;
        }
    }

    /// <summary>The live session ids, for an actionable "unknown session" message.</summary>
    public string DescribeKnownSessions()
    {
        lock (_gate)
        {
            return _sessions.Count == 0 ? "(none)" : string.Join(", ", _sessions.Keys.Order(StringComparer.Ordinal));
        }
    }

    /// <summary>Disposes and forgets the session. Returns false when the id is unknown.</summary>
    public bool DisposeSession(string sessionId)
    {
        SessionHandle? handle;
        lock (_gate)
        {
            if (!_sessions.Remove(sessionId, out handle))
            {
                return false;
            }
        }

        handle.Dispose();
        return true;
    }

    /// <summary>Disposes every session. Called by the host when the client disconnects.</summary>
    public void Dispose()
    {
        List<SessionHandle> remaining;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            remaining = [.. _sessions.Values];
            _sessions.Clear();
        }

        _sweeper.Dispose();

        foreach (var handle in remaining)
        {
            handle.Dispose();
        }
    }

    private void Sweep()
    {
        List<SessionHandle> expired = [];

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var (id, handle) in _sessions.ToArray())
            {
                if (handle.IsIdle(_idleTimeout))
                {
                    _sessions.Remove(id);
                    expired.Add(handle);
                }
            }
        }

        foreach (var handle in expired)
        {
            handle.Dispose();
        }
    }
}

/// <summary>A registered session together with the lock that serializes its operations.</summary>
public sealed class SessionHandle : IDisposable
{
    private readonly AppSession _session;
    private long _lastUsedTicks;

    internal SessionHandle(string id, AppSession session)
    {
        Id = id;
        _session = session;
        _lastUsedTicks = Environment.TickCount64;
    }

    /// <summary>The generated session id, e.g. <c>s1</c>.</summary>
    public string Id { get; }

    /// <summary>The session.</summary>
    public AppSession Session => _session;

    /// <summary>Serializes operations on this session.</summary>
    public object Gate { get; } = new();

    internal void Touch() => Interlocked.Exchange(ref _lastUsedTicks, Environment.TickCount64);

    internal bool IsIdle(TimeSpan timeout)
        => Environment.TickCount64 - Interlocked.Read(ref _lastUsedTicks) >= timeout.TotalMilliseconds;

    /// <summary>Disposes the underlying session (closing its trace and stopping an owned process).</summary>
    public void Dispose()
    {
        try
        {
            _session.Dispose();
        }
        catch (Exception)
        {
            // A teardown failure must never take the server down.
        }
    }
}
