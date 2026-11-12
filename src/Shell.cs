using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace WarpSwitch;

/// A problem the user can act on; its message is shown as-is.
sealed class UserError : Exception
{
    public UserError(string message) : base(message) { }
}

static class Shell
{
    public static int Run(string exe, string args, int timeoutMs = 15000) => Capture(exe, args, null, timeoutMs).Code;

    /// Runs a console tool hidden and returns its exit code and combined output.
    public static (int Code, string Output) Capture(string exe, string args, string workDir, int timeoutMs)
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
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(); } catch { }
            return (-1, "timed out");
        }
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}

/// Rolling log: the last few hundred lines in memory, mirrored to a small capped file (app.log).
sealed class LogBuffer
{
    const long MaxFileBytes = 256 * 1024;
    readonly Queue<string> lines = new();
    readonly int max;
    readonly string file;

    public LogBuffer(int max, string file = null)
    {
        this.max = max;
        this.file = file;
        try
        {
            if (file != null) Directory.CreateDirectory(Path.GetDirectoryName(file));
            if (file != null && File.Exists(file) && new FileInfo(file).Length > MaxFileBytes)
            {
                File.Delete(file + ".1");     // keep exactly one previous log
                File.Move(file, file + ".1");
            }
        }
        catch { }
    }

    public void Add(string line)
    {
        string stamped = DateTime.Now.ToString("HH:mm:ss  ") + line;
        lock (lines)
        {
            lines.Enqueue(stamped);
            while (lines.Count > max) lines.Dequeue();
            if (file == null) return;
            try { File.AppendAllText(file, DateTime.Now.ToString("yyyy-MM-dd ") + stamped + Environment.NewLine); } catch { }
        }
    }

    public string Text()
    {
        lock (lines) return string.Join(Environment.NewLine, lines);
    }

    /// Last line that came from usque itself (app status lines start with "·").
    public string LastToolLine()
    {
        lock (lines) return lines.Select(l => l.Substring(10)).LastOrDefault(l => !l.StartsWith("·")) ?? "";
    }
}
