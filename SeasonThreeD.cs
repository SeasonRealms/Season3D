// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonThreeD

namespace Season.ThreeD;

/// <summary>
/// Entry point for the trellis.cpp image-to-3D pipeline.
///
/// The transport is a native library, not a child process: trellis.cpp exports no C
/// header, so this repository contributes one thin shim (<c>native/trellis_capi.cpp</c>)
/// whose single entry <c>trellis_run_argv</c> runs the same <c>parse_args()</c> +
/// <c>trellis_run()</c> pair the trellis-cli worker runs. A dll on Windows, a .so on
/// Linux, a .dylib on macOS - one library-shaped API everywhere.
///
/// The library is published as two lanes, because trellis's two custom GPU kernels
/// (BiRefNet's deformable conv and the QEM decimator) are compiled against exactly one
/// graphics API. This is not a ggml choice: ggml's backends are modules chosen at run
/// time, while these two kernels are trellis's own and are baked in at compile time.
/// <c>SeasonImage</c> and <c>SeasonLLM</c> have no such kernels, which is why they ship
/// a single library and pick a backend by name.
/// <list type="bullet">
/// <item><c>trellis</c> - the CUDA lane: both kernels are native CUDA, which is the fast
/// path on an NVIDIA card. It imports <c>cudart64_12.dll</c>, so it only loads where
/// <c>cudacudnn-runtime</c> is staged.</item>
/// <item><c>trellis-vulkan</c> - the Vulkan lane: both kernels are SPIR-V, so one binary
/// covers NVIDIA, AMD and Intel and needs no part of the CUDA runtime.</item>
/// </list>
///
/// <see cref="TrellisWorker.Auto"/> - the default - loads the CUDA lane when the NVIDIA
/// driver is present and the Vulkan lane otherwise, so a machine with an AMD or Intel
/// GPU gets the Vulkan lane without being asked. Both lanes fall back to ggml's CPU
/// backend when no device initializes, and
/// <see cref="TrellisSessionOptions.Backend"/> pins the host explicitly.
///
/// The library shares the product's ggml runtime instead of carrying its own: it is
/// built with <c>GGML_BACKEND_DL=ON</c>, so the loader takes <c>ggml.dll</c> and
/// <c>ggml-base.dll</c> from the application's own directory and
/// <c>ggml_backend_load_all</c> scans that same directory for the
/// <c>ggml-cpu-&lt;variant&gt;</c> / <c>ggml-cuda</c> / <c>ggml-vulkan</c> backend modules.
/// The library is therefore staged in the output root, where SeasonGGML copies its
/// runtime, and a NuGet consumer gets both in <c>runtimes&lt;rid&gt;\native</c>. That is
/// why this package references SeasonGGML and ships no ggml dlls of its own: two copies
/// in one folder cannot coexist (the loader binds the first), and every AI package in
/// the process has to agree on one ggml build anyway.
///
/// Two limits come with the in-process lane: a native fault is not isolated by a process
/// boundary, and trellis has no cancellation or deadline hook, so a run always finishes
/// (cancellation and the run budget are reported once it returns). The mode is
/// desktop-only: Android, iOS and Mac Catalyst report
/// <see cref="PlatformNotSupportedException"/>.
/// </summary>
public static class SeasonThreeD
{
    private static readonly object s_logLock = new();
    private static Action<TrellisLogLevel, string>? s_logCallback;

    /// <summary>Whether the current process can host the trellis library.</summary>
    public static bool IsSupported =>
        OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>Whether a lane library could be located (it may still fail to load).</summary>
    public static bool IsWorkerAvailable => DefaultLibraryPath is not null;

    /// <summary>The lane library that <see cref="CreateSession"/> would pick, or null when absent.</summary>
    public static string? DefaultLibraryPath => TrellisRuntimeLocator.TryResolve(TrellisWorker.Auto, null);

    /// <summary>
    /// The lanes whose library is actually installed, in the order
    /// <see cref="TrellisWorker.Auto"/> prefers them on this machine. Lets a caller offer
    /// "CUDA or Vulkan" where both were staged - or the two lanes side by side in a
    /// diagnostics view - without loading either.
    /// </summary>
    public static IReadOnlyList<TrellisWorker> AvailableWorkers => TrellisRuntimeLocator.AvailableWorkers();

    /// <summary>
    /// Receives every line the native library writes to stdout/stderr. Mirrors
    /// <c>StableDiffusion.SetLogCallback</c> and <c>SeasonLLM.SetLogCallback</c>: one
    /// process-wide sink, replaced on each call, with <see langword="null"/> detaching
    /// it. Runs on the descriptor reader thread, so keep it short and marshal to the UI
    /// thread yourself.
    /// </summary>
    public static void SetLogCallback(Action<TrellisLogLevel, string>? callback)
    {
        lock (s_logLock)
        {
            s_logCallback = callback;
        }
    }

    /// <summary>
    /// Resolves the path of the lane <see cref="TrellisWorker.Auto"/> would load, without
    /// loading it. An explicit path wins; then <c>SEASONTHREED_TRELLIS_LIBRARY</c> (or the
    /// older <c>SEASONTHREED_TRELLIS_CLI</c>), the output root,
    /// <c>runtimes/&lt;rid&gt;/native</c> and PATH.
    /// </summary>
    public static string ResolveLibrary(string? libraryPath = null)
        => TrellisRuntimeLocator.Resolve(TrellisWorker.Auto, libraryPath);

    /// <summary>
    /// Resolves one specific lane without loading it: <c>trellis.dll</c> for
    /// <see cref="TrellisWorker.Cuda"/>, <c>trellis-vulkan.dll</c> for
    /// <see cref="TrellisWorker.Vulkan"/> (<c>libtrellis*.so</c> / <c>.dylib</c>
    /// elsewhere). The environment override is not consulted here - it names one file and
    /// cannot tell the lanes apart - so pass a path if the build lives somewhere unusual.
    /// </summary>
    public static string ResolveLibrary(TrellisWorker worker, string? libraryPath = null)
        => TrellisRuntimeLocator.Resolve(worker, libraryPath);

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
    /// Loads the <see cref="TrellisWorker.Auto"/> library and asks it for <c>--help</c>,
    /// reporting what answered. A cheap way to turn "is 3D installed and runnable here?"
    /// into a concrete answer before a run that would otherwise fail minutes into loading
    /// 20+ GB of weights. Loading the image is the check: a missing ggml or cudart import
    /// fails here, not inside the first generation.
    /// </summary>
    public static Task<TrellisRuntimeInfo> ProbeAsync(
        string? libraryPath = null,
        CancellationToken cancellationToken = default)
        => ProbeAsync(TrellisWorker.Auto, libraryPath, cancellationToken);

    /// <summary>Same probe, for one specific lane - the way to tell a CUDA install from a Vulkan one.</summary>
    public static async Task<TrellisRuntimeInfo> ProbeAsync(
        TrellisWorker worker,
        string? libraryPath = null,
        CancellationToken cancellationToken = default)
    {
        EnsureSupported();

        var resolved = TrellisRuntimeLocator.Resolve(worker, libraryPath);
        var outcome = await TrellisNativeRunner.RunAsync(
            resolved,
            ["--help"],
            environmentVariables: null,
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
            LibraryPath = resolved,
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
                "SeasonThreeD loads the native trellis library, which ships for Windows, Linux and macOS " +
                $"only; this process targets {RuntimeInformation.RuntimeIdentifier}.");
        }
    }
}
