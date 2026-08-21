namespace Libreguard.Vpn.Linux.Services;

internal static class StartupDiagnostics
{
    private static readonly Lock SyncRoot = new();
    private static readonly string LogPath = ResolveLogPath();

    public static string StartupLogPath => LogPath;

    public static void Log(string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {message}";

        try
        {
            Console.Error.WriteLine(line);
        }
        catch
        {
        }

        try
        {
            lock (SyncRoot)
            {
                FileSecurity.AppendPrivateText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
        }
    }

    public static IDisposable WatchStep(string stepName, TimeSpan? warningAfter = null)
        => new StartupStepScope(stepName, warningAfter ?? TimeSpan.FromSeconds(10));

    private static string ResolveLogPath()
    {
        try
        {
            return XdgPaths.StartupLogFilePath;
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory, "startup.log");
        }
    }

    private sealed class StartupStepScope : IDisposable
    {
        private readonly string _stepName;
        private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
        private readonly PeriodicTimer _timer;
        private readonly CancellationTokenSource _cts = new();
        private int _completed;

        public StartupStepScope(string stepName, TimeSpan warningAfter)
        {
            _stepName = stepName;
            _timer = new PeriodicTimer(warningAfter);

            Log($"startup-step-begin step=\"{_stepName}\"");
            _ = MonitorAsync();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0)
            {
                return;
            }

            _cts.Cancel();
            _timer.Dispose();
            _cts.Dispose();

            var elapsed = DateTimeOffset.UtcNow - _startedAt;
            Log($"startup-step-end step=\"{_stepName}\" elapsed_ms={elapsed.TotalMilliseconds:F0}");
        }

        private async Task MonitorAsync()
        {
            try
            {
                while (await _timer.WaitForNextTickAsync(_cts.Token))
                {
                    if (Volatile.Read(ref _completed) != 0)
                    {
                        return;
                    }

                    var elapsed = DateTimeOffset.UtcNow - _startedAt;
                    Log($"startup-step-still-running step=\"{_stepName}\" elapsed_ms={elapsed.TotalMilliseconds:F0}");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
