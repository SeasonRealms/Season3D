// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/Season3D

namespace Season.ThreeD;

/// <summary>
/// Which flow weights the model directory holds. Both families share the TRELLIS.2
/// DiT, sampler and decoders; they differ only in how the input image conditions the
/// flow, so one GGUF directory can serve both (<c>--model</c>).
/// </summary>
public enum TrellisModelFamily
{
    /// <summary>TRELLIS.2: cross-attention over every DINOv3 token (the default).</summary>
    Trellis = 0,

    /// <summary>Pixal3D: 5 global tokens plus a per-token view-aligned projection.</summary>
    Pixal3D = 1
}

/// <summary>Geometry resolution (<c>--res</c>).</summary>
public enum TrellisResolution
{
    /// <summary>Light single-resolution path; Pixal3D degrades to geometry only here.</summary>
    R512 = 512,

    /// <summary>Default 1024 cascade.</summary>
    R1024 = 1024,

    /// <summary>1536 cascade (same weights, larger HR grid).</summary>
    R1536 = 1536
}

/// <summary>Background removal mode (<c>--bg-removal</c>).</summary>
public enum TrellisBackgroundRemoval
{
    /// <summary>
    /// Keep a pre-matted image's alpha; otherwise BiRefNet when <c>birefnet.gguf</c> is
    /// present; the white-threshold matte as a last resort.
    /// </summary>
    Auto = 0,

    /// <summary>White-threshold matte. Reads specular highlights as background.</summary>
    Threshold = 1,

    /// <summary>BiRefNet neural matte (<c>--birefnet</c>). Needs <c>birefnet.gguf</c>.</summary>
    BiRefNet = 2
}

/// <summary>UV unwrap mode.</summary>
public enum TrellisUvUnwrap
{
    /// <summary>xatlas unwrap (the default, <c>--xatlas</c>).</summary>
    XAtlas = 0,

    /// <summary>Voxel-native box projection, faster (<c>--box-uv</c>).</summary>
    Box = 1
}

/// <summary>GLB texture encoding (<c>--webp</c>).</summary>
public enum TrellisWebpEncoding
{
    /// <summary>WebP when the worker was built with WebP support, PNG otherwise.</summary>
    Auto = 0,

    /// <summary>Force WebP.</summary>
    On = 1,

    /// <summary>Force PNG.</summary>
    Off = 2
}

/// <summary>FlashAttention precision policy (<c>--fa-fast</c> / <c>--fa-f32</c>).</summary>
public enum TrellisFlashAttentionMode
{
    /// <summary>F16 fast on HTP, BF16 K/V + F32 accumulation elsewhere (the default).</summary>
    Auto = 0,

    /// <summary>Force F16 K/V + fast accumulation.</summary>
    Fast = 1,

    /// <summary>Force BF16 K/V + F32 accumulation.</summary>
    F32 = 2
}

/// <summary>
/// Pipeline stage reported by <see cref="TrellisProgress"/>. The worker banners are
/// numbered inconsistently upstream (1-3 as "/6", 4-7 as "/7"), so the enum is the
/// stable contract and the raw numbers ride along in <see cref="TrellisProgress"/>.
/// </summary>
public enum TrellisStage
{
    /// <summary>Nothing reported yet (the worker is still starting up).</summary>
    Idle = 0,

    /// <summary>[1/6] background removal + normalization.</summary>
    Preprocess = 1,

    /// <summary>[2/6] DINOv3 image conditioning.</summary>
    Conditioning = 2,

    /// <summary>[3/6] sparse-structure flow + decode.</summary>
    SparseStructure = 3,

    /// <summary>[4/7] shape SLAT flow (LR 512, and the HR cascade pass).</summary>
    ShapeFlow = 4,

    /// <summary>[5/7] FlexiDualGrid shape decode to a mesh.</summary>
    ShapeDecode = 5,

    /// <summary>[6/7] texture SLAT flow + PBR decode.</summary>
    Texture = 6,

    /// <summary>[7/7] remesh, QEM decimation, UV bake and GLB writing.</summary>
    Postprocess = 7,

    /// <summary>The worker printed its final "done in ..." line.</summary>
    Completed = 8
}

/// <summary>Severity attached to a line forwarded to <see cref="Season3D.SetLogCallback"/>.</summary>
public enum TrellisLogLevel
{
    /// <summary>Standard output (stage banners, sampler progress, timings).</summary>
    Info = 0,

    /// <summary>Standard error (seed announcement, warnings, failure details).</summary>
    Warning = 1,

    /// <summary>Reserved for the managed side to mark a failed run.</summary>
    Error = 2
}
