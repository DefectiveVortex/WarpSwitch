using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace WarpSwitch;

/// A problem the user can act on; its message is shown as-is.
sealed class UserError : Exception
{
    public UserError(string message) : base(message) { }
}

static class Shell
{
    public static int Run(string exe, string args, int timeoutMs = 15000) => Capture(exe, args, null, timeoutMs).Code;

    /// Runs a console tool hidden and returns its exit code and combined output. Cancelling kills the tool and
    /// throws <see cref="OperationCanceledException"/>.
    public static (int Code, string Output) Capture(string exe, string args, string workDir, int timeoutMs, CancellationToken cancel = default)
    {
        if (!Path.IsPathRooted(exe)) exe = Path.Combine(Environment.SystemDirectory, exe);
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDir ?? Environment.SystemDirectory,
        };
        using var p = Process.Start(psi);
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        var clock = Stopwatch.StartNew();
        while (!p.WaitForExit(200))
        {
            if (!cancel.IsCancellationRequested && clock.ElapsedMilliseconds < timeoutMs) continue;
            try { p.Kill(); } catch { }
            cancel.ThrowIfCancellationRequested();
            return (-1, "timed out");
        }
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}

/// Rolling log: the last few hundred lines in memory, mirrored to a small capped file (app.log, plus one
/// previous app.log.1). A line repeated back-to-back is counted instead of stored: usque can print the same
/// error hundreds of times a second when a connection dies, which used to flood both.
sealed class LogBuffer : IDisposable
{
    public const long MaxFileBytes = 256 * 1024;
    readonly Queue<string> lines = new();
    readonly int max;
    readonly string file;
    long added;              // lines ever added (the position Since() works with)
    string lastCollapsible;  // previous line, if it came through AddCollapsed
    int repeats;
    StreamWriter writer;
    long fileBytes;

    public LogBuffer(int max, string file = null)
    {
        this.max = max;
        this.file = file;
    }

    public void Add(string line) => Add(line, false);

    /// For tool output: a line identical to the previous one becomes a "repeated N times" note.
    public void AddCollapsed(string line) => Add(line, true);

    void Add(string line, bool collapse)
    {
        lock (lines)
        {
            if (collapse && line == lastCollapsible) { repeats++; return; }
            FlushRepeatsLocked();
            lastCollapsible = collapse ? line : null;
            AppendLocked(line);
        }
    }

    void FlushRepeatsLocked()
    {
        if (repeats == 0) return;
        int n = repeats;
        repeats = 0;
        AppendLocked("  ⋯ repeated " + n + (n == 1 ? " more time" : " more times"));
    }

    void AppendLocked(string line)
    {
        var now = DateTime.Now;
        string stamped = now.ToString("HH:mm:ss  ") + line;
        lines.Enqueue(stamped);
        added++;
        while (lines.Count > max) lines.Dequeue();
        WriteFileLocked(now.ToString("yyyy-MM-dd ") + stamped);
    }

    void WriteFileLocked(string text)
    {
        if (file == null) return;
        try
        {
            if (writer == null) OpenLocked();
            if (fileBytes > MaxFileBytes)
            {
                writer.Dispose();
                writer = null;
                Rotate();
                OpenLocked();
            }
            writer.WriteLine(text);
            fileBytes += Encoding.UTF8.GetByteCount(text) + 2;
        }
        catch
        {
            try { writer?.Dispose(); } catch { }
            writer = null;
        }
    }

    void OpenLocked()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file));
        if (File.Exists(file) && new FileInfo(file).Length > MaxFileBytes) Rotate();
        var fs = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fileBytes = fs.Length;
        writer = new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
    }

    void Rotate()
    {
        File.Delete(file + ".1");     // keep exactly one previous log
        File.Move(file, file + ".1");
    }

    public string Text()
    {
        lock (lines)
        {
            FlushRepeatsLocked();
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// Lines added since <paramref name="after"/> (a position from an earlier call; 0 at first). <paramref name="reset"/>
    /// is set when the caller fell so far behind that some lines are gone: it then gets everything still kept.
    public string[] Since(long after, out long position, out bool reset)
    {
        lock (lines)
        {
            FlushRepeatsLocked();
            position = added;
            long oldest = added - lines.Count;
            reset = after < oldest;
            return lines.Skip(reset ? 0 : (int)(after - oldest)).ToArray();
        }
    }

    /// Last line that came from usque itself (app status lines start with "·", repeat notes with "⋯").
    public string LastToolLine()
    {
        lock (lines)
            return lines.Select(l => l.Substring(10)).LastOrDefault(l => !l.StartsWith("·") && !l.TrimStart().StartsWith("⋯")) ?? "";
    }

    public void Dispose()
    {
        lock (lines)
        {
            FlushRepeatsLocked();
            try { writer?.Dispose(); } catch { }
            writer = null;
        }
    }
}
