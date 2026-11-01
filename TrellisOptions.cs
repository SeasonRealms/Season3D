// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonThreeD

namespace Season.ThreeD;

/// <summary>
/// Per-session knobs: where the native library lives, which GGUF directory it reads, and
/// the device selection that applies to every run of the session. These map to the
/// trellis argv that does not change per image.
/// </summary>
public sealed class TrellisSessionOptions
{
    /// <summary>
    /// Native library. <see langword="null"/> resolves the lane <see cref="Worker"/> asks
    /// for next to the managed assembly (flat output root, then
    /// <c>runtimes/&lt;rid&gt;/native</c>) and finally on PATH.
    /// </summary>
    /// <remarks>
    /// An explicit path always wins, including over <see cref="Worker"/>: the lane is
    /// then read off the file name, so pointing this at a local
    /// <c>trellis-vulkan.dll</c> selects the Vulkan lane without setting
    /// <see cref="Worker"/> as well.
    /// </remarks>
    public string? LibraryPath { get; set; }

    /// <summary>
    /// Which shipped lane the session loads. <see cref="TrellisWorker.Auto"/> - the
    /// default - takes the CUDA lane on a machine with the NVIDIA driver and the Vulkan
    /// lane everywhere else. See <see cref="SeasonThreeD.AvailableWorkers"/> for what is
    /// actually installed.
    /// </summary>
    public TrellisWorker Worker { get; set; } = TrellisWorker.Auto;

    /// <summary>GGUF model directory (<c>--models</c>). Required.</summary>
    public string ModelsPath { get; set; } = "models";

    /// <summary>
    /// GPU index (<c>--gpu</c>); a negative value runs on the CPU. On the Vulkan lane a
    /// negative value also suppresses the implicit <c>--backend Vulkan</c>, because an
    /// explicit <c>--backend</c> outranks <c>--gpu</c> inside the worker.
    /// </summary>
    public int GpuIndex { get; set; }

    /// <summary>
    /// Force one ggml backend by registry name (<c>--backend</c>): <c>CUDA</c>,
    /// <c>Vulkan</c>, <c>CPU</c>. Empty - the default - lets the worker choose, which is
    /// what most callers want: the CUDA lane runs on CUDA and falls back to CPU on its
    /// own, and the Vulkan lane does the same for Vulkan.
    /// </summary>
    /// <remarks>
    /// The Vulkan lane is the one case the session decides for you. SeasonGGML ships
    /// <c>ggml-cuda.dll</c> in the same folder, so on an NVIDIA machine a Vulkan worker
    /// left to itself would initialize the CUDA device - right for ggml, wrong for
    /// trellis, whose deformable-conv and QEM kernels in that build are SPIR-V. The
    /// session therefore passes <c>--backend Vulkan</c> itself, retries once on
    /// <c>CPU</c> if that backend will not initialize, and only stands aside when this
    /// property or <see cref="GpuIndex"/> already says what to do.
    /// </remarks>
    public string? Backend { get; set; }

    /// <summary>
    /// CPU backend thread count (<c>--threads</c>). 0 lets ggml use all cores; ggml's
    /// own default is 4 regardless of core count.
    /// </summary>
    public int ThreadCount { get; set; }

    /// <summary>Per-stage timings and per-step diagnostics (<c>--verbose</c>).</summary>
    public bool Verbose { get; set; }

    /// <summary>
    /// Wall-clock budget for one run. <see langword="null"/> waits indefinitely - the
    /// 1024/1536 cascade legitimately takes tens of minutes on a CPU backend.
    /// </summary>
    /// <remarks>
    /// A budget, not a kill switch: the native lane has no deadline hook, so a run that
    /// overruns still finishes and the oversized duration is reported as
    /// <see cref="TimeoutException"/> afterwards.
    /// </remarks>
    public TimeSpan? RunTimeout { get; set; }

    /// <summary>
    /// Extra environment variables for the run (the <c>TRELLIS_*</c> switches that have
    /// no argv flag, e.g. <c>TRELLIS_QEM_PRESERVE_TOPOLOGY</c>). They are set for the
    /// duration of the call and restored afterwards.
    /// </summary>
    public IDictionary<string, string>? EnvironmentVariables { get; set; }
}

/// <summary>
/// One image-to-3D request. Every property maps to a trellis command-line flag; a
/// <see langword="null"/> optional value leaves trellis' own default in place
/// rather than pinning a value that would drift if upstream changes it.
/// </summary>
public sealed class TrellisGenerationOptions
{
    /// <summary>Input image (<c>--image</c>). PNG/JPEG; a pre-matted alpha is kept.</summary>
    public string ImagePath { get; set; } = string.Empty;

    /// <summary>Output GLB (<c>--output</c>). Its directory is created when missing.</summary>
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>Which flow weights the model directory holds (<c>--model</c>).</summary>
    public TrellisModelFamily ModelFamily { get; set; } = TrellisModelFamily.Trellis;

    /// <summary>Geometry resolution (<c>--res</c>).</summary>
    public TrellisResolution Resolution { get; set; } = TrellisResolution.R1024;

    /// <summary>Flow sampler steps (<c>--steps</c>). 0 keeps the model default (12).</summary>
    public int Steps { get; set; }

    /// <summary>
    /// RNG seed (<c>--seed</c>). 0 asks the worker for a random seed; the value it
    /// picked is reported back on <see cref="TrellisResult.Seed"/>.
    /// </summary>
    public uint Seed { get; set; }

    /// <summary>HR token budget (<c>--max-tokens</c>). <see langword="null"/> keeps 49152.</summary>
    public int? MaxTokens { get; set; }

    /// <summary>Background removal mode (<c>--bg-removal</c> / <c>--birefnet</c>).</summary>
    public TrellisBackgroundRemoval BackgroundRemoval { get; set; } = TrellisBackgroundRemoval.Auto;

    /// <summary>Run the texture flow and bake (<c>--no-texture</c> when false).</summary>
    public bool Texture { get; set; } = true;

    /// <summary>UV unwrap mode (<c>--box-uv</c> when <see cref="TrellisUvUnwrap.Box"/>).</summary>
    public TrellisUvUnwrap UvUnwrap { get; set; } = TrellisUvUnwrap.XAtlas;

    /// <summary>Narrow-band DC remesh band width (<c>--band</c>); null = resolution-derived.</summary>
    public int? Band { get; set; }

    /// <summary>
    /// QEM decimation face target (<c>--faces</c>), at least 1000. Null keeps the
    /// per-cascade default (300K at 1024/1536, 150K at 512). Mutually exclusive with
    /// <see cref="DecimationGrid"/>.
    /// </summary>
    /// <remarks>50 000 is a good game-prop budget.</remarks>
    public int? Faces { get; set; }

    /// <summary>
    /// Legacy cluster-grid decimation instead of QEM (<c>--decim</c>); 0 keeps the full
    /// resolution mesh. Null keeps the QEM path.
    /// </summary>
    public int? DecimationGrid { get; set; }

    /// <summary>UV atlas size in pixels (<c>--atlas</c>); null = 2048 @1024, 1024 @512.</summary>
    public int? AtlasSize { get; set; }

    /// <summary>PBR texture resolution (<c>--tex-res</c>): 512 or 1024; null = auto.</summary>
    public int? TextureResolution { get; set; }

    /// <summary>GLB texture encoding (<c>--webp</c>).</summary>
    public TrellisWebpEncoding Webp { get; set; } = TrellisWebpEncoding.Auto;

    /// <summary>Sparse-structure guidance strength (<c>--gss</c>); null = 7.5.</summary>
    public float? SparseStructureGuidance { get; set; }

    /// <summary>Shape-SLAT guidance strength (<c>--gsh</c>); null = 7.5.</summary>
    public float? ShapeGuidance { get; set; }

    /// <summary>Multi-backend scheduler (<c>--sched</c>); null = on unless the backend is CPU.</summary>
    public bool? Scheduler { get; set; }

    /// <summary>Vulkan fallback for HTP runs (<c>--vulkan-fallback</c> / <c>--no-vulkan-fallback</c>).</summary>
    public bool? VulkanFallback { get; set; }

    /// <summary>f32 sparse-conv compute (<c>--f32</c>).</summary>
    public bool ForceF32 { get; set; }

    /// <summary>Disable FlashAttention and use the manual softmax path (<c>--no-fa</c>).</summary>
    public bool DisableFlashAttention { get; set; }

    /// <summary>FlashAttention precision policy (<c>--fa-fast</c> / <c>--fa-f32</c>).</summary>
    public TrellisFlashAttentionMode FlashAttention { get; set; } = TrellisFlashAttentionMode.Auto;

    /// <summary>Refuse CPU fallback when no GPU is usable (<c>--require-gpu</c>).</summary>
    public bool RequireGpu { get; set; }

    /// <summary>Also write the background-removal cutout as <c>&lt;output&gt;_cutout.png</c>.</summary>
    public bool DumpBackgroundCutout { get; set; }

    /// <summary>
    /// Background removal only: write the cutout and stop (<c>--bg-only</c>). Useful for
    /// a cheap "is the subject isolated?" preview before committing to a full run.
    /// </summary>
    public bool BackgroundRemovalOnly { get; set; }

    /// <summary>Also dump the voxel point cloud (<c>--voxply</c>).</summary>
    public bool DumpVoxelPointCloud { get; set; }

    /// <summary>Dump the structured latent to disk (<c>--dump-slat</c>).</summary>
    public bool DumpStructuredLatent { get; set; }

    /// <summary>glTF <c>asset.copyright</c> metadata (<c>--copyright</c>).</summary>
    public string? Copyright { get; set; }

    /// <summary>
    /// Pixal3D: run NAF guided upsampling of the DINOv3 feature map. False is the
    /// <c>--no-naf</c> escape hatch (no <c>naf.gguf</c> needed, lower quality).
    /// </summary>
    public bool UseNaf { get; set; } = true;

    /// <summary>
    /// Pixal3D: horizontal FOV of the input image in degrees (<c>--fov</c>); null keeps
    /// Pixal3D's own 49.13 (MoGe-2 estimation is not ported).
    /// </summary>
    public float? FovDegrees { get; set; }

    /// <summary>Pixal3D: object scale inside the unit grid (<c>--mesh-scale</c>); null = 1.</summary>
    public float? MeshScale { get; set; }

    /// <summary>Pixal3D: push the camera's virtual border outward by N 512-frame pixels.</summary>
    public int? ExtendPixel { get; set; }
}
