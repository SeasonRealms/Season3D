// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonThreeD

namespace Season.ThreeD;

/// <summary>
/// Entry point for the trellis.cpp image-to-3D pipeline.
///
/// Unlike <c>SeasonImage</c> and <c>SeasonLLM</c> this is not a P/Invoke wrapper:
/// trellis.cpp exports no C ABI, it ships <c>trellis-cli</c> (one image in, one GLB
/// out) and <c>trellis-server</c> (the resident HTTP form). The managed surface mirrors
/// the sibling packages - a static facade, an options object per session, a context
/// class that owns one pipeline - while the transport is a child process.
///
/// The worker is published as two lanes, because trellis's two custom GPU kernels
/// (BiRefNet's deformable conv and the QEM decimator) are compiled against exactly one
/// graphics API:
/// <list type="bullet">
/// <item><c>trellis-cli</c> - the CUDA lane: both kernels are native CUDA, which is the
/// fast path on an NVIDIA card, and the worker refuses every non-CUDA GPU device so it
/// can never run a kernel it did not build.</item>
/// <item><c>trellis-cli-vulkan</c> - the Vulkan lane: both kernels are SPIR-V, so one
/// binary covers NVIDIA, AMD and Intel and needs no part of the CUDA runtime.</item>
/// </list>
///
/// <see cref="TrellisWorker.Auto"/> - the default - runs the CUDA lane when the NVIDIA
/// driver is present and the Vulkan lane otherwise, so a machine with an AMD or Intel
/// GPU gets the Vulkan lane without being asked. Both lanes fall back to ggml's CPU
/// backend when no device initializes, and
/// <see cref="TrellisSessionOptions.Backend"/> pins the host explicitly. A card whose
/// Vulkan support is incomplete is a run-time property of that driver: the lane still
/// starts and the CPU fallback still runs.
///
/// The worker shares the product's ggml runtime instead of carrying its own: it is
/// built with <c>GGML_BACKEND_DL=ON</c>, so the PE loader takes <c>ggml.dll</c> and
/// <c>ggml-base.dll</c> from the executable's own directory and
/// <c>ggml_backend_load_all</c> scans that same directory (then the working directory)
/// for the <c>ggml-cpu-&lt;variant&gt;</c> / <c>ggml-cuda</c> / <c>ggml-vulkan</c> backend
/// modules. This project therefore stages the worker in the output root, where
/// SeasonGGML copies its runtime, and a NuGet consumer gets both in
/// <c>runtimes&lt;rid&gt;\native</c>. That is why this package references SeasonGGML and
/// ships no ggml dlls of its own: two copies in one folder cannot coexist (the loader
/// binds the first), and every AI package in the process has to agree on one ggml
/// build anyway.
///
/// The mode is intentionally desktop-only: the worker is a native executable, so
/// Android, iOS and Mac Catalyst report <see cref="PlatformNotSupportedException"/>.
/// </summary>
public static class SeasonThreeD
{
    private static readonly object s_logLock = new();
    private static Action<TrellisLogLevel, string>? s_logCallback;

    /// <summary>Whether the current process can host the trellis worker.</summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>Whether a worker executable could be located (it may still fail to start).</summary>
    public static bool IsWorkerAvailable => DefaultExecutablePath is not null;

    /// <summary>The worker that <see cref="CreateSession"/> would pick, or null when absent.</summary>
    public static string? DefaultExecutablePath => TrellisRuntimeLocator.TryResolve(TrellisWorker.Auto, null);

    /// <summary>
    /// The lanes whose worker is actually installed, in the order
    /// <see cref="TrellisWorker.Auto"/> prefers them on this machine. Lets a caller offer
    /// "CUDA or Vulkan" where both were staged - or the two lanes side by side in a
    /// diagnostics view - without starting either.
    /// </summary>
    public static IReadOnlyList<TrellisWorker> AvailableWorkers => TrellisRuntimeLocator.AvailableWorkers();

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
    /// Resolves the path of the lane <see cref="TrellisWorker.Auto"/> would run, without
    /// starting it. An explicit path wins; then <c>SEASONTHREED_TRELLIS_CLI</c>, the
    /// output root, <c>runtimes/&lt;rid&gt;/native</c> and PATH.
    /// </summary>
    public static string ResolveExecutable(string? executablePath = null)
        => TrellisRuntimeLocator.Resolve(TrellisWorker.Auto, executablePath);

    /// <summary>
    /// Resolves one specific lane without starting it: <c>trellis-cli</c> for
    /// <see cref="TrellisWorker.Cuda"/>, <c>trellis-cli-vulkan</c> for
    /// <see cref="TrellisWorker.Vulkan"/>. The environment override is not consulted here
    /// - it names one file and cannot tell the lanes apart - so pass a path if the build
    /// lives somewhere unusual.
    /// </summary>
    public static string ResolveExecutable(TrellisWorker worker, string? executablePath = null)
        => TrellisRuntimeLocator.Resolve(worker, executablePath);

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
    /// Starts the <see cref="TrellisWorker.Auto"/> worker with <c>--help</c> and reports
    /// what answered. A cheap way to turn "is 3D installed and runnable here?" into a
    /// concrete answer before a run that would otherwise fail minutes into loading 20+ GB
    /// of weights.
    /// </summary>
    public static Task<TrellisRuntimeInfo> ProbeAsync(
        string? executablePath = null,
        CancellationToken cancellationToken = default)
        => ProbeAsync(TrellisWorker.Auto, executablePath, cancellationToken);

    /// <summary>Same probe, for one specific lane - the way to tell a CUDA install from a Vulkan one.</summary>
    public static async Task<TrellisRuntimeInfo> ProbeAsync(
        TrellisWorker worker,
        string? executablePath = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSupported();

        var resolved = TrellisRuntimeLocator.Resolve(worker, executablePath);
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
            Worker = TrellisRuntimeLocator.DetectLane(resolved),
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
                "SeasonThreeD drives the native trellis-cli worker, which ships for Windows, Linux and macOS " +
                $"only; this process targets {RuntimeInformation.RuntimeIdentifier}.");
        }
    }
}
