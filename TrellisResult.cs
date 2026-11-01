// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonThreeD

namespace Season.ThreeD;

/// <summary>
/// What one successful image-to-3D run produced. All paths are absolute; the side
/// artifacts are only reported when the worker actually wrote them.
/// </summary>
public sealed class TrellisResult
{
    /// <summary>The requested GLB (<c>--output</c>).</summary>
    public required string OutputPath { get; init; }

    /// <summary>
    /// <c>&lt;output&gt;_base.png</c>: the baked PBR atlas, written next to the GLB when
    /// the texture path ran.
    /// </summary>
    public string? TexturePath { get; init; }

    /// <summary>
    /// <c>&lt;output&gt;.ply</c>: the full-resolution (pre-decimation) mesh with vertex
    /// colours, always written by the worker.
    /// </summary>
    public string? PointCloudPath { get; init; }

    /// <summary>
    /// <c>&lt;output&gt;_cutout.png</c>: the background-removal matte, written when
    /// <see cref="TrellisGenerationOptions.DumpBackgroundCutout"/> or
    /// <see cref="TrellisGenerationOptions.BackgroundRemovalOnly"/> is set.
    /// </summary>
    public string? CutoutPath { get; init; }

    /// <summary>
    /// The seed the run actually used. Equals <see cref="TrellisGenerationOptions.Seed"/>
    /// unless that was 0, in which case this is the random seed the worker picked - keep
    /// it to reproduce the mesh.
    /// </summary>
    public uint Seed { get; init; }

    /// <summary>Wall-clock time of the native run.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Return code of <c>trellis_run_argv</c> (0 for a completed run).</summary>
    public int ExitCode { get; init; }

    /// <summary>Captured stdout/stderr lines, in arrival order (capped).</summary>
    public IReadOnlyList<string> Log { get; init; } = [];

    internal static TrellisResult FromRun(TrellisGenerationOptions options, TrellisRunOutcome outcome)
    {
        var output = Path.GetFullPath(options.OutputPath);
        var directory = Path.GetDirectoryName(output) ?? string.Empty;
        var stem = Path.Combine(directory, Path.GetFileNameWithoutExtension(output));

        return new TrellisResult
        {
            OutputPath = output,
            TexturePath = Existing(stem + "_base.png"),
            // trellis derives the side artifacts from the output path by truncating
            // it at the last dot, so this is exactly <stem>.ply / <stem>_base.png.
            PointCloudPath = Existing(stem + ".ply"),
            CutoutPath = Existing(stem + "_cutout.png"),
            Seed = outcome.Seed != 0 ? outcome.Seed : options.Seed,
            Duration = outcome.Duration,
            ExitCode = outcome.ExitCode,
            Log = outcome.Log
        };
    }

    private static string? Existing(string path) => File.Exists(path) ? path : null;
}

/// <summary>
/// Identity of a trellis native build: enough for the UI to show "which lane am I
/// running?" without a native version call (the shim has no version command).
/// </summary>
public sealed class TrellisRuntimeInfo
{
    /// <summary>Resolved native library path.</summary>
    public required string LibraryPath { get; init; }

    /// <summary>
    /// The lane that library belongs to, read off its file name - so a probe reports
    /// which of the two lanes actually answered, not just that one did.
    /// </summary>
    public TrellisWorker Worker { get; init; }

    /// <summary>Win32/dylib file version, when the linker stamped one.</summary>
    public string? FileVersion { get; init; }

    /// <summary>Product version, when the linker stamped one.</summary>
    public string? ProductVersion { get; init; }

    /// <summary>Return code of the <c>--help</c> probe (0 means the library loaded and parsed it).</summary>
    public int ExitCode { get; init; }

    /// <summary>Everything the library printed for <c>--help</c> (usage goes to stderr).</summary>
    public string HelpText { get; init; } = string.Empty;

    /// <summary>Whether the probe completed with exit code 0.</summary>
    public bool IsUsable => ExitCode == 0;
}
