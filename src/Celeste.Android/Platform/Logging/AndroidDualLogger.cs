using System;
using System.Globalization;
using System.IO;
using System.Text;
using Celeste.Core.Platform.Logging;

namespace Celeste.Android.Platform.Logging;

public sealed class AndroidDualLogger : IAppLogger, IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    public AndroidDualLogger(string logsPath)
    {
        Directory.CreateDirectory(logsPath);
        var sessionName = $"log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt";
        CurrentSessionLogFile = Path.Combine(logsPath, sessionName);
        _writer = new StreamWriter(new FileStream(CurrentSessionLogFile, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false))
        {
            AutoFlush = true
        };
    }

    public string CurrentSessionLogFile { get; }

    public void Log(LogLevel level, string tag, string message, Exception? exception = null, string? context = null)
    {
        var safeTag = string.IsNullOrWhiteSpace(tag) ? "CELESTE" : tag;
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        var line = $"{timestamp} | {level} | {safeTag} | {message}";

        if (!string.IsNullOrWhiteSpace(context))
        {
            line += $" | {context}";
        }

        if (exception is not null)
        {
            line += Environment.NewLine + exception;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _writer.WriteLine(line);
        }

        switch (level)
        {
            case LogLevel.Info:
                global::Android.Util.Log.Info(safeTag, line);
                break;
            case LogLevel.Warn:
                global::Android.Util.Log.Warn(safeTag, line);
                break;
            default:
                global::Android.Util.Log.Error(safeTag, line);
                break;
        }
    }

    public void Flush()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _writer.Flush();
            _writer.Dispose();
            _disposed = true;
        }
    }
}
