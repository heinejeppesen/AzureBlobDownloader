using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AzureBlobDownloader.Core.Diagnostics;

/// <summary>
/// Keeps the computer from going to sleep while downloading (the screen may still turn off).
/// <list type="bullet">
///   <item>Windows: <c>SetThreadExecutionState(ES_SYSTEM_REQUIRED)</c> on a dedicated thread.</item>
///   <item>macOS: <c>caffeinate -i -w &lt;pid&gt;</c>, which also ends if the app exits unexpectedly.</item>
///   <item>Linux: <c>systemd-inhibit --what=sleep:idle</c> around a watcher that ends when the app exits.</item>
/// </list>
/// Failures are logged and ignored: downloads work without it, the computer just may sleep.
/// </summary>
public static class KeepAwake
{
    private const string Reason = "Downloading files";

    /// <summary>Keeps the computer awake until the returned object is disposed.</summary>
    public static IDisposable Acquire()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return new WindowsRequest();
            }

            var pid = Environment.ProcessId;
            if (OperatingSystem.IsMacOS())
            {
                return new HelperProcess("caffeinate", ["-i", "-w", pid.ToString()]);
            }

            if (OperatingSystem.IsLinux())
            {
                return new HelperProcess("systemd-inhibit",
                [
                    "--what=sleep:idle", "--who=Azure Blob Downloader", $"--why={Reason}", "--mode=block",
                    "sh", "-c", $"while kill -0 {pid} 2>/dev/null; do sleep 5; done",
                ]);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Cannot keep the computer awake: {ex.GetType().Name}: {ex.Message}");
        }

        return new Nothing();
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>SetThreadExecutionState is per thread, so a dedicated thread holds it until released.</summary>
    private sealed class WindowsRequest : IDisposable
    {
        private const uint EsContinuous = 0x80000000;
        private const uint EsSystemRequired = 0x00000001;

        private readonly ManualResetEventSlim _release = new();

        public WindowsRequest()
        {
            var thread = new Thread(() =>
            {
                if (SetThreadExecutionState(EsContinuous | EsSystemRequired) == 0)
                {
                    AppLog.Warn("SetThreadExecutionState failed; the computer may sleep during the download.");
                }

                _release.Wait();
                SetThreadExecutionState(EsContinuous);
            })
            {
                IsBackground = true,
                Name = "Keep awake",
            };
            thread.Start();
        }

        public void Dispose() => _release.Set();

        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);
    }

    private sealed class HelperProcess : IDisposable
    {
        private readonly Process? _process;

        public HelperProcess(string fileName, string[] arguments)
        {
            var start = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            _process = Process.Start(start);
        }

        public void Dispose()
        {
            try
            {
                if (_process is { HasExited: false })
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or ExternalException)
            {
                // Already gone.
            }
            finally
            {
                _process?.Dispose();
            }
        }
    }
}
