using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;

namespace DeskDx;

/// <summary>
/// Playwright-style call log of an <see cref="AppSession"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every session owns exactly one trace directory
/// <c>&lt;TraceDirectory&gt;/&lt;yyyyMMdd-HHmmss-fffffff&gt;/</c> containing
/// <c>trace.jsonl</c> (one JSON object per event: <c>{ ts, level, indent, message }</c>)
/// and <c>shots/</c> (screenshots).
/// </para>
/// <para>
/// <see cref="Phase"/> opens an indented phase and increases the indentation of every
/// following line by two spaces until the next <see cref="Phase"/> call, which renders
/// exactly the Schritt 0 / Playwright call log layout. <see cref="ToCallLog"/> renders
/// that layout <em>without</em> timestamps (the JSONL keeps them).
/// </para>
/// </remarks>
public sealed class TraceLog
{
    /// <summary>Base indentation (in spaces) of a rendered call log line.</summary>
    private const int BaseIndent = 2;

    private readonly object _gate = new();
    private readonly List<TraceEvent> _events = new();
    private readonly string _sessionDirectory;
    private readonly string _shotsDirectory;
    private readonly StreamWriter _writer;
    private int _indent = BaseIndent;
    private int _suppressDepth;
    private bool _closed;

    internal TraceLog(string sessionDirectory)
    {
        _sessionDirectory = sessionDirectory;
        _shotsDirectory = Path.Combine(sessionDirectory, "shots");
        Directory.CreateDirectory(_shotsDirectory);

        var traceFile = Path.Combine(sessionDirectory, "trace.jsonl");
        // FileShare.ReadWrite keeps the trace readable (tests, tools) while the session runs.
        var stream = new FileStream(traceFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            // The trace must be complete on disk even if the session is never disposed.
            AutoFlush = true,
        };
    }

    /// <summary>
    /// Creates a new per-session trace directory below <paramref name="traceDirectory"/>.
    /// </summary>
    internal static TraceLog CreateSession(string? traceDirectory)
    {
        var root = string.IsNullOrWhiteSpace(traceDirectory) ? "traces" : traceDirectory;
        var sessionDirectory = Path.Combine(
            root,
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(sessionDirectory);
        return new TraceLog(sessionDirectory);
    }

    /// <summary>
    /// The directory holding this session's trace (<c>trace.jsonl</c> and <c>shots/</c>).
    /// The same path is reported in <see cref="DeskDxCrashError"/> messages.
    /// </summary>
    public string SessionDirectory => _sessionDirectory;

    /// <summary>Opens an indented phase, e.g. "waiting for locator(...)".</summary>
    /// <remarks>
    /// <para>
    /// <b>Flat phases (Step 2):</b> every phase line is written at the base level (2 spaces) and
    /// its children follow at +2. Phases do <em>not</em> accumulate indentation: a phase opened
    /// after another phase starts again at the base level, matching the Playwright call log where
    /// each "waiting for …" attempt restarts at base.
    /// </para>
    /// </remarks>
    public void Phase(string message)
    {
        lock (_gate)
        {
            _indent = BaseIndent;
            Append("phase", _indent, message);
            _indent = BaseIndent + 2;
        }
    }

    /// <summary>Line within the current phase, e.g. "locator resolved to ...".</summary>
    public void Log(string message) => InternalLog(message);

    /// <summary>
    /// Restores the base indentation, ending the current phase. Idempotent: calling it when no phase
    /// is open (or twice) simply leaves the indentation at the base level.
    /// </summary>
    internal void EndPhase()
    {
        lock (_gate)
        {
            _indent = BaseIndent;
        }
    }

    /// <summary>
    /// Records an internal line at the current indentation. Used by <see cref="AppSession"/> and the
    /// locator so their bookkeeping messages do not influence the public phase indentation.
    /// </summary>
    internal void InternalLog(string message)
    {
        lock (_gate)
        {
            Append("log", _indent, message);
        }
    }

    /// <summary>
    /// Emits an internal (library) phase together with its already known lines, then restores the
    /// base indentation so the next phase starts flat.
    /// </summary>
    internal void InternalPhase(string message, IReadOnlyList<string> lines)
    {
        lock (_gate)
        {
            _indent = BaseIndent;
            Append("phase", _indent, message);
            foreach (var line in lines)
            {
                Append("log", BaseIndent + 2, line);
            }

            _indent = BaseIndent;
        }
    }

    /// <summary>
    /// Records an internal line one level deeper than the current phase children. Used for the
    /// nested `scope children:` entries of a locator timeout dump.
    /// </summary>
    internal void InternalDetail(string message)
    {
        lock (_gate)
        {
            Append("log", _indent + 2, message);
        }
    }

    /// <summary>
    /// Screenshot (FlaUI.Capture) into the trace directory; <paramref name="element"/> = null
    /// captures the whole desktop.
    /// </summary>
    public void Screenshot(AutomationElement? element = null, string? label = null)
    {
        lock (_gate)
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff", CultureInfo.InvariantCulture);
            var fileName = string.IsNullOrWhiteSpace(label)
                ? stamp + ".png"
                : stamp + "-" + SanitizeLabel(label!) + ".png";
            var path = Path.Combine(_shotsDirectory, fileName);

            using (var capture = element is null ? Capture.Screen() : Capture.Element(element))
            {
                capture.ToFile(path);
            }

            var relativePath = "shots/" + fileName;
            var message = string.IsNullOrWhiteSpace(label)
                ? $"screenshot saved: {relativePath}"
                : $"screenshot saved: {relativePath} ({label})";
            Append("screenshot", _indent, message);
        }
    }

    /// <summary>Formatted call log (for timeout/crash error attachments).</summary>
    public IReadOnlyList<string> ToCallLog()
    {
        lock (_gate)
        {
            var lines = new string[_events.Count];
            for (var i = 0; i < _events.Count; i++)
            {
                lines[i] = FormatLine(_events[i]);
            }

            return lines;
        }
    }

    /// <summary>
    /// Suppresses persistence until the returned scope is disposed. The in-memory call log keeps
    /// growing, so a failure can still render full evidence, but a successful assertion leaves no
    /// trace behind (Playwright rule: the log shows the problem, not the functioning).
    /// </summary>
    internal IDisposable SuppressPersistence()
    {
        lock (_gate)
        {
            _suppressDepth++;
        }

        return new Suppression(this);
    }

    private void EndSuppression()
    {
        lock (_gate)
        {
            _suppressDepth = Math.Max(0, _suppressDepth - 1);
        }
    }

    /// <summary>Drops everything recorded after the given call log mark.</summary>
    internal void TruncateTo(int mark)
    {
        lock (_gate)
        {
            if (mark >= 0 && mark < _events.Count)
            {
                _events.RemoveRange(mark, _events.Count - mark);
            }
        }
    }

    /// <summary>Number of recorded call log events; used as a mark by the assertions.</summary>
    internal int EventCount
    {
        get
        {
            lock (_gate)
            {
                return _events.Count;
            }
        }
    }

    /// <summary>Flushes and releases the underlying trace file.</summary>
    internal void Close()
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            _writer.Flush();
            _writer.Dispose();
        }
    }

    private static string FormatLine(TraceEvent traceEvent)
        => new string(' ', traceEvent.Indent) + "- " + traceEvent.Message;

    private static string SanitizeLabel(string label)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(label.Length);
        foreach (var character in label)
        {
            builder.Append(Array.IndexOf(invalid, character) >= 0 ? '_' : character);
        }

        return builder.ToString();
    }

    private void Append(string level, int indent, string message)
    {
        var traceEvent = new TraceEvent(
            DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture),
            level,
            indent,
            message);

        _events.Add(traceEvent);

        // Auto-flush normally keeps the trace complete on disk even without a clean Dispose;
        // inside a suppression scope (assertions) nothing reaches the file until it succeeds-or-fails.
        if (_suppressDepth == 0)
        {
            _writer.WriteLine(JsonSerializer.Serialize(traceEvent));
        }
    }

    private sealed class Suppression(TraceLog owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            owner.EndSuppression();
        }
    }

    private sealed record TraceEvent(
        [property: JsonPropertyName("ts")] string Ts,
        [property: JsonPropertyName("level")] string Level,
        [property: JsonPropertyName("indent")] int Indent,
        [property: JsonPropertyName("message")] string Message);
}
