// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/Season3D

namespace Season.ThreeD;

/// <summary>
/// Entry point for the trellis.cpp image-to-3D pipeline.
///
/// Unlike <c>SeasonImage</c> and <c>SeasonLLM</c> this is not a P/Invoke wrapper:
/// trellis.cpp exports no C ABI, it ships <c>trellis-cli</c> (one image in, one GLB
/// out) and <c>trellis-server</c> (the resident HTTP form). The managed surface mirrors
/// the sibling packages - a static facade, an options object per session, a context
/// class that owns one pipeline - while the transport is a child process, so the
/// worker can be rebuilt on its own ggml baseline without touching the shared
/// SeasonGGML runtime.
///
/// The mode is intentionally desktop-only: the worker is a native executable, so
/// Android, iOS and Mac Catalyst report <see cref="PlatformNotSupportedException"/>.
/// </summary>
public static class Season3D
{
    private static readonly object s_logLock = new();
    private static Action<TrellisLogLevel, string>? s_logCallback;

    /// <summary>Whether the current process can host the trellis-cli worker.</summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>Whether a worker executable could be located (it may still fail to start).</summary>
    public static bool IsWorkerAvailable => DefaultExecutablePath is not null;

    /// <summary>The worker that <see cref="CreateSession"/> would pick, or null when absent.</summary>
    public static string? DefaultExecutablePath => TrellisRuntimeLocator.TryResolve(null);

    /// <summary>
    /// Receives every worker line. Mirrors <c>StableDiffusion.SetLogCallback</c> and
    /// <c>SeasonLLM.SetLogCallback</c>: one process-wide sink, replaced on each call,
    /// with <see langword="null"/> detaching it. Runs on the stream-pump thread, so keep
    /// it short and marshal to the UI thread yourself.
    /// </summary>
    public static void SetLogCallback(Action<TrellisLogLevel, string>? callback)
    {
        lock (s_logLock)
        {
            s_logCallback = callback;
        }
    }

    /// <summary>
    /// Resolves the trellis-cli path without starting it. An explicit path wins; then
    /// <c>SEASON3D_TRELLIS_CLI</c>, the output root, <c>runtimes/&lt;rid&gt;/native</c>
    /// and PATH.
    /// </summary>
    public static string ResolveExecutable(string? executablePath = null)
        => TrellisRuntimeLocator.Resolve(executablePath);

    /// <summary>
    /// Creates one pipeline binding (model directory, device, threads). One session can
    /// run any number of images; runs are serialized because each one loads GGUF
    /// weights per stage and two concurrent runs would multiply peak VRAM/RAM.
    /// </summary>
    public static TrellisSession CreateSession(TrellisSessionOptions options)
    {
        EnsureSupported();
        ArgumentNullException.ThrowIfNull(options);
        return new TrellisSession(options);
    }

    /// <summary>
    /// One-shot convenience overload: creates a session, runs the image, disposes the
    /// session. Use <see cref="CreateSession"/> when several images share a model
    /// directory.
    /// </summary>
    public static async Task<TrellisResult> GenerateAsync(
        TrellisSessionOptions sessionOptions,
        TrellisGenerationOptions generationOptions,
        IProgress<TrellisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var session = CreateSession(sessionOptions);
        try
        {
            return await session.GenerateAsync(generationOptions, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>
    /// Starts the worker with <c>--help</c> and reports what answered. A cheap way to
    /// turn "is 3D installed and runnable here?" into a concrete answer before a run
    /// that would otherwise fail minutes into loading 20+ GB of weights.
    /// </summary>
    public static async Task<TrellisRuntimeInfo> ProbeAsync(
        string? executablePath = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSupported();

        var resolved = TrellisRuntimeLocator.Resolve(executablePath);
        var outcome = await TrellisProcessRunner.RunAsync(
            resolved,
            ["--help"],
            Path.GetDirectoryName(resolved),
            environment: null,
            timeout: TimeSpan.FromSeconds(60),
            LogCallback,
            progress: null,
            cancellationToken).ConfigureAwait(false);

        string? fileVersion = null;
        string? productVersion = null;
        try
        {
            var version = FileVersionInfo.GetVersionInfo(resolved);
            fileVersion = string.IsNullOrWhiteSpace(version.FileVersion) ? null : version.FileVersion;
            productVersion = string.IsNullOrWhiteSpace(version.ProductVersion) ? null : version.ProductVersion;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A worker without version resources is still a worker; the probe result
            // below already says whether it ran.
        }

        return new TrellisRuntimeInfo
        {
            ExecutablePath = resolved,
            FileVersion = fileVersion,
            ProductVersion = productVersion,
            ExitCode = outcome.ExitCode,
            HelpText = string.Join(Environment.NewLine, outcome.Log)
        };
    }

    internal static Action<TrellisLogLevel, string>? LogCallback
    {
        get
        {
            lock (s_logLock)
            {
                return s_logCallback;
            }
        }
    }

    internal static void EnsureSupported()
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException(
                "Season3D drives the native trellis-cli worker, which ships for Windows, Linux and macOS " +
                $"only; this process targets {RuntimeInformation.RuntimeIdentifier}.");
        }
    }
}
