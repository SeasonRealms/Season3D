// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/Season3D

namespace Season.ThreeD;

/// <summary>
/// One binding to the trellis.cpp worker: a resolved executable, one GGUF model
/// directory, and the device/thread choices every run of this session uses. The shape
/// follows <c>StableDiffusionContext</c> and <c>SeasonLlmModel</c> - construct through
/// <see cref="Season3D.CreateSession"/>, keep it while the model directory is in use,
/// dispose it when done.
/// </summary>
/// <remarks>
/// There is no native handle to own (each run is a short-lived process), so this type
/// is cheap; the <see cref="SemaphoreSlim"/> exists only to serialize concurrent
/// <see cref="GenerateAsync"/> calls, because a second worker would load a second copy
/// of the weights.
/// </remarks>
public sealed class TrellisSession : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    internal TrellisSession(TrellisSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ModelsPath);

        var modelsPath = Path.GetFullPath(options.ModelsPath);
        if (!Directory.Exists(modelsPath))
        {
            throw new DirectoryNotFoundException(
                $"The trellis GGUF model directory was not found: {modelsPath}. " +
                "A TRELLIS.2 set needs dinov3.gguf, ss_flow.gguf, ss_dec.gguf, shape_flow_512.gguf, " +
                "shape_dec.gguf and the texture decoders; BiRefNet adds birefnet.gguf.");
        }

        Options = options;
        ModelsPath = modelsPath;
        ExecutablePath = TrellisRuntimeLocator.Resolve(options.ExecutablePath);
        WorkingDirectory = string.IsNullOrWhiteSpace(options.WorkingDirectory)
            ? Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory
            : Path.GetFullPath(options.WorkingDirectory);
    }

    /// <summary>The resolved trellis-cli executable this session runs.</summary>
    public string ExecutablePath { get; }

    /// <summary>The absolute GGUF model directory.</summary>
    public string ModelsPath { get; }

    /// <summary>The worker's working directory (its own directory by default).</summary>
    public string WorkingDirectory { get; }

    /// <summary>The options this session was created with.</summary>
    public TrellisSessionOptions Options { get; }

    /// <summary>
    /// Runs one image through the pipeline. Throws <see cref="InvalidOperationException"/>
    /// when the worker exits non-zero (the message carries its trailing
    /// <c>[trellis]</c> lines), <see cref="OperationCanceledException"/> when
    /// <paramref name="cancellationToken"/> fires (the worker process tree is killed),
    /// and <see cref="TimeoutException"/> when <see cref="TrellisSessionOptions.RunTimeout"/>
    /// expires.
    /// </summary>
    public async Task<TrellisResult> GenerateAsync(
        TrellisGenerationOptions options,
        IProgress<TrellisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);

        // Validate and build the command line before taking the lock: a bad request
        // should not queue behind a 20-minute run just to fail.
        var arguments = TrellisCommandLine.Build(Options, options);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var outcome = await TrellisProcessRunner.RunAsync(
                ExecutablePath,
                arguments,
                WorkingDirectory,
                Options.EnvironmentVariables is null
                    ? null
                    : new Dictionary<string, string>(Options.EnvironmentVariables),
                Options.RunTimeout,
                Season3D.LogCallback,
                progress,
                cancellationToken).ConfigureAwait(false);

            if (outcome.ExitCode != 0)
            {
                throw new InvalidOperationException(BuildFailureMessage(outcome));
            }

            return TrellisResult.FromRun(options, outcome);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs the <c>--help</c> probe against this session's executable.</summary>
    public Task<TrellisRuntimeInfo> ProbeAsync(CancellationToken cancellationToken = default)
        => Season3D.ProbeAsync(ExecutablePath, cancellationToken);

    /// <summary>
    /// Marks the session unusable. An in-flight run is left to finish or cancel on its
    /// own cancellation token - disposal never blocks on it.
    /// </summary>
    public void Dispose() => _disposed = true;

    private static string BuildFailureMessage(TrellisProcessOutcome outcome)
    {
        var builder = new StringBuilder();
        builder.Append("trellis-cli exited with code ");
        builder.Append(outcome.ExitCode.ToString(CultureInfo.InvariantCulture));
        builder.Append(" after ");
        builder.Append(outcome.Duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture));
        builder.Append('s');

        if (outcome.ErrorLines.Count > 0)
        {
            builder.AppendLine(":");
            foreach (var line in outcome.ErrorLines)
            {
                builder.AppendLine(line);
            }
        }
        else
        {
            builder.Append('.');
        }

        return builder.ToString();
    }
}
