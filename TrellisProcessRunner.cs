// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/Season3D

// Process.Start/Kill are unsupported on iOS and Mac Catalyst, which this package still
// compiles for (SeasonStudio references every AI package from all of its heads). The only
// entry points - Season3D.CreateSession/ProbeAsync - call EnsureSupported() first, so no
// worker can reach this code there; the analyzer cannot see across that guard.
#pragma warning disable CA1416

namespace Season.ThreeD;

/// <summary>Everything one trellis-cli invocation reported back.</summary>
internal sealed class TrellisProcessOutcome
{
    public int ExitCode { get; init; }

    public uint Seed { get; init; }

    public TimeSpan Duration { get; init; }

    public IReadOnlyList<string> Log { get; init; } = [];

    /// <summary>The trailing stderr lines, used to explain a non-zero exit.</summary>
    public IReadOnlyList<string> ErrorLines { get; init; } = [];
}

/// <summary>
/// Runs the worker and pumps its two streams. Both are read concurrently because the
/// pipeline interleaves machine-readable progress (stdout) with the seed announcement
/// and failure details (stderr), and a synchronous ReadToEnd on either would deadlock
/// once the other pipe's buffer filled.
/// </summary>
internal static class TrellisProcessRunner
{
    // A capped log keeps a 1536-cascade verbose run (thousands of per-step lines) from
    // pinning memory in the UI; the tail is what diagnostics needs anyway.
    private const int MaximumLogLines = 20_000;

    public static async Task<TrellisProcessOutcome> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        TimeSpan? timeout,
        Action<TrellisLogLevel, string>? logCallback,
        IProgress<TrellisProgress>? progress,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start the trellis-cli worker at '{executablePath}'.");
            }
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Failed to start the trellis-cli worker at '{executablePath}': {exception.Message}",
                exception);
        }

        var parser = new TrellisOutputParser();
        var log = new List<string>();
        var logLock = new object();
        var stdioState = new StdioState(parser, log, logLock, logCallback, progress, stopwatch, cancellationToken);

        var stdoutTask = PumpAsync(process.StandardOutput, isStandardError: false, stdioState);
        var stderrTask = PumpAsync(process.StandardError, isStandardError: true, stdioState);

        try
        {
            await WaitForExitAsync(process, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await DrainAsync(process).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }
        catch (TimeoutException)
        {
            Kill(process);
            await DrainAsync(process).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        // The process exited, but the pumps may still hold buffered lines.
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        stopwatch.Stop();

        IReadOnlyList<string> errorLines;
        lock (logLock)
        {
            errorLines = log
                .Where(static line => line.TrimStart().StartsWith("[trellis]", StringComparison.Ordinal))
                .TakeLast(8)
                .ToArray();
        }

        return new TrellisProcessOutcome
        {
            ExitCode = process.ExitCode,
            Seed = parser.Seed,
            Duration = stopwatch.Elapsed,
            Log = log.ToArray(),
            ErrorLines = errorLines
        };
    }

    private static async Task WaitForExitAsync(Process process, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = timeout is { } budget ? new CancellationTokenSource(budget) : null;
        using var linkedSource = timeoutSource is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && timeoutSource is { IsCancellationRequested: true })
        {
            throw new TimeoutException(
                $"trellis-cli did not finish within {timeout!.Value.TotalMinutes:0.#} minutes and was terminated.");
        }
    }

    private static async Task DrainAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // The process never started or was already reaped; nothing left to drain.
        }
    }

    private static async Task PumpAsync(StreamReader reader, bool isStandardError, StdioState state)
    {
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(state.CancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            if (line is null)
            {
                return;
            }

            TrellisProgressUpdate update = default;
            var hasUpdate = false;

            lock (state.LogLock)
            {
                if (state.Log.Count < MaximumLogLines)
                {
                    state.Log.Add(line);
                }

                hasUpdate = state.Parser.TryParse(line, out update);
            }

            state.LogCallback?.Invoke(
                isStandardError ? TrellisLogLevel.Warning : TrellisLogLevel.Info,
                line);

            if (hasUpdate)
            {
                state.Progress?.Report(TrellisProgress.FromUpdate(update, state.Stopwatch.Elapsed));
            }
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone between the check and the kill.
        }
        catch (NotSupportedException)
        {
            // entireProcessTree is unsupported on a few hosts; the direct kill is enough.
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private sealed class StdioState
    {
        public StdioState(
            TrellisOutputParser parser,
            List<string> log,
            object logLock,
            Action<TrellisLogLevel, string>? logCallback,
            IProgress<TrellisProgress>? progress,
            Stopwatch stopwatch,
            CancellationToken cancellationToken)
        {
            Parser = parser;
            Log = log;
            LogLock = logLock;
            LogCallback = logCallback;
            Progress = progress;
            Stopwatch = stopwatch;
            CancellationToken = cancellationToken;
        }

        public TrellisOutputParser Parser { get; }

        public List<string> Log { get; }

        public object LogLock { get; }

        public Action<TrellisLogLevel, string>? LogCallback { get; }

        public IProgress<TrellisProgress>? Progress { get; }

        public Stopwatch Stopwatch { get; }

        public CancellationToken CancellationToken { get; }
    }
}

#pragma warning restore CA1416
