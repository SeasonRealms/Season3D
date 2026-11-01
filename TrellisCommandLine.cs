// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonThreeD

namespace Season.ThreeD;

/// <summary>
/// Translates the managed options into trellis arguments. Validation mirrors what
/// <c>trellis_args.cpp</c> enforces so that a bad request fails here, with a managed
/// stack trace and no native call, instead of surfacing as exit code 1 plus a
/// one-line usage dump.
/// </summary>
internal static class TrellisCommandLine
{
    // Mirrors kQemFacesMin in trellis_args.h: below this the QEM simplifier collapses
    // thin parts outright instead of failing, so the worker rejects it.
    private const int MinimumFaceTarget = 1000;

    public static IReadOnlyList<string> Build(
        TrellisSessionOptions session,
        TrellisGenerationOptions options,
        TrellisWorker worker,
        string? backendOverride = null)
    {
        Validate(session, options);

        var image = Path.GetFullPath(options.ImagePath);
        var output = Path.GetFullPath(options.OutputPath);

        var args = new List<string>(64)
        {
            "--image", image,
            "--output", output,
            "--models", Path.GetFullPath(session.ModelsPath),
            "--gpu", session.GpuIndex.ToString(CultureInfo.InvariantCulture),
            "--res", ((int)options.Resolution).ToString(CultureInfo.InvariantCulture)
        };

        // The retry's override, then whatever this lane has to pin on its own (see
        // ForcedBackend), then the caller's own value.
        var backend = backendOverride ?? ForcedBackend(session, worker) ?? session.Backend?.Trim();
        if (!string.IsNullOrWhiteSpace(backend))
        {
            args.Add("--backend");
            args.Add(backend);
        }

        if (session.ThreadCount > 0)
        {
            args.Add("--threads");
            args.Add(session.ThreadCount.ToString(CultureInfo.InvariantCulture));
        }

        if (session.Verbose)
        {
            args.Add("--verbose");
        }

        if (options.Steps > 0)
        {
            args.Add("--steps");
            args.Add(options.Steps.ToString(CultureInfo.InvariantCulture));
        }

        // Always pass the seed: 0 is the worker's explicit "pick one for me", and the
        // picked value comes back on the "generating model with seed N (auto)" line.
        args.Add("--seed");
        args.Add(options.Seed.ToString(CultureInfo.InvariantCulture));

        if (options.MaxTokens is int maxTokens)
        {
            args.Add("--max-tokens");
            args.Add(maxTokens.ToString(CultureInfo.InvariantCulture));
        }

        switch (options.BackgroundRemoval)
        {
            case TrellisBackgroundRemoval.Threshold:
                args.Add("--bg-removal");
                args.Add("threshold");
                break;
            case TrellisBackgroundRemoval.BiRefNet:
                args.Add("--birefnet");
                break;
        }

        if (!options.Texture)
        {
            args.Add("--no-texture");
        }

        if (options.UvUnwrap == TrellisUvUnwrap.Box)
        {
            args.Add("--box-uv");
        }

        if (options.Band is int band)
        {
            args.Add("--band");
            args.Add(band.ToString(CultureInfo.InvariantCulture));
        }

        if (options.Faces is int faces)
        {
            args.Add("--faces");
            args.Add(faces.ToString(CultureInfo.InvariantCulture));
        }

        if (options.DecimationGrid is int decimation)
        {
            args.Add("--decim");
            args.Add(decimation.ToString(CultureInfo.InvariantCulture));
        }

        if (options.AtlasSize is int atlas)
        {
            args.Add("--atlas");
            args.Add(atlas.ToString(CultureInfo.InvariantCulture));
        }

        if (options.TextureResolution is int textureResolution)
        {
            args.Add("--tex-res");
            args.Add(textureResolution.ToString(CultureInfo.InvariantCulture));
        }

        switch (options.Webp)
        {
            case TrellisWebpEncoding.On:
                args.Add("--webp");
                args.Add("on");
                break;
            case TrellisWebpEncoding.Off:
                args.Add("--webp");
                args.Add("off");
                break;
        }

        if (options.SparseStructureGuidance is float gss)
        {
            args.Add("--gss");
            args.Add(FormatFloat(gss));
        }

        if (options.ShapeGuidance is float gsh)
        {
            args.Add("--gsh");
            args.Add(FormatFloat(gsh));
        }

        if (options.Scheduler is bool scheduler)
        {
            args.Add("--sched");
            args.Add(scheduler ? "on" : "off");
        }

        if (options.VulkanFallback is bool vulkanFallback)
        {
            args.Add(vulkanFallback ? "--vulkan-fallback" : "--no-vulkan-fallback");
        }

        if (options.ForceF32)
        {
            args.Add("--f32");
        }

        if (options.DisableFlashAttention)
        {
            args.Add("--no-fa");
        }
        else
        {
            switch (options.FlashAttention)
            {
                case TrellisFlashAttentionMode.Fast:
                    args.Add("--fa-fast");
                    break;
                case TrellisFlashAttentionMode.F32:
                    args.Add("--fa-f32");
                    break;
            }
        }

        if (options.RequireGpu)
        {
            args.Add("--require-gpu");
        }

        if (options.DumpBackgroundCutout)
        {
            args.Add("--dump-bg");
        }

        if (options.BackgroundRemovalOnly)
        {
            args.Add("--bg-only");
        }

        if (options.DumpVoxelPointCloud)
        {
            args.Add("--voxply");
        }

        if (options.DumpStructuredLatent)
        {
            args.Add("--dump-slat");
        }

        if (!string.IsNullOrWhiteSpace(options.Copyright))
        {
            args.Add("--copyright");
            args.Add(options.Copyright);
        }

        // trellis is the worker's default family; only the Pixal3D switch is explicit.
        if (options.ModelFamily == TrellisModelFamily.Pixal3D)
        {
            args.Add("--model");
            args.Add("pixal3d");
        }

        if (!options.UseNaf)
        {
            args.Add("--no-naf");
        }

        if (options.FovDegrees is float fov)
        {
            args.Add("--fov");
            args.Add(FormatFloat(fov));
        }

        if (options.MeshScale is float meshScale)
        {
            args.Add("--mesh-scale");
            args.Add(FormatFloat(meshScale));
        }

        if (options.ExtendPixel is int extendPixel)
        {
            args.Add("--extend-pixel");
            args.Add(extendPixel.ToString(CultureInfo.InvariantCulture));
        }

        return args;
    }

    /// <summary>
    /// The backend this build pins on the caller's behalf, or null when the caller already
    /// decided. Only the Vulkan lane needs one: it shares an output folder with SeasonGGML's
    /// <c>ggml-cuda.dll</c>, so left alone the worker would initialize the CUDA device while
    /// its deformable-conv and QEM kernels are SPIR-V. A negative <c>GpuIndex</c> is an
    /// explicit CPU request and outranks the lane default.
    /// </summary>
    /// <remarks>
    /// Returned rather than folded into <see cref="Build"/> because pinning the backend also
    /// takes the worker's own CPU fallback away; the caller re-creates it (one retry with
    /// <c>--backend CPU</c>) and needs to know whether it pinned anything.
    /// </remarks>
    internal static string? ForcedBackend(TrellisSessionOptions session, TrellisWorker worker)
    {
        if (!string.IsNullOrWhiteSpace(session.Backend))
        {
            return null;
        }

        if (session.GpuIndex < 0)
        {
            return null;
        }

        return worker == TrellisWorker.Vulkan ? "Vulkan" : null;
    }

    private static void Validate(TrellisSessionOptions session, TrellisGenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);

        ArgumentException.ThrowIfNullOrWhiteSpace(options.ImagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.OutputPath);

        var image = Path.GetFullPath(options.ImagePath);
        if (!File.Exists(image))
        {
            throw new FileNotFoundException($"Input image not found: {image}", image);
        }

        var output = Path.GetFullPath(options.OutputPath);
        var outputDirectory = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        if (session.GpuIndex < -1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(session), session.GpuIndex, "GpuIndex must be -1 (CPU) or a non-negative device index.");
        }

        if (session.ThreadCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(session), session.ThreadCount, "ThreadCount must be >= 0.");
        }

        if (session.RunTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(session), timeout, "RunTimeout must be positive.");
        }

        if (!Enum.IsDefined(options.Resolution))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Resolution, "Unsupported geometry resolution.");
        }

        if (options.Steps < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Steps, "Steps must be >= 0.");
        }

        if (options.MaxTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxTokens, "MaxTokens must be positive.");
        }

        if (options.Band is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Band, "Band must be >= 0.");
        }

        // check_faces_compat() in trellis_args.cpp: --faces steers the QEM target, so it
        // cannot be combined with any --decim value (-1 is the "unset" sentinel there).
        if (options.Faces is not null && options.DecimationGrid is not null)
        {
            throw new ArgumentException(
                "Faces and DecimationGrid are mutually exclusive; trellis rejects --faces together with --decim.",
                nameof(options));
        }

        if (options.Faces is int faces && faces < MinimumFaceTarget)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), faces, $"Faces must be at least {MinimumFaceTarget} to keep thin parts intact.");
        }

        if (options.DecimationGrid is int decimation && decimation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), decimation, "DecimationGrid must be >= 0.");
        }

        if (options.AtlasSize is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.AtlasSize, "AtlasSize must be positive.");
        }

        if (options.TextureResolution is int textureResolution and not (512 or 1024))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), textureResolution, "TextureResolution must be 512 or 1024.");
        }

        if (options.SparseStructureGuidance is float gss && (!float.IsFinite(gss) || gss <= 0.0f))
        {
            throw new ArgumentOutOfRangeException(nameof(options), gss, "SparseStructureGuidance must be finite and positive.");
        }

        if (options.ShapeGuidance is float gsh && (!float.IsFinite(gsh) || gsh <= 0.0f))
        {
            throw new ArgumentOutOfRangeException(nameof(options), gsh, "ShapeGuidance must be finite and positive.");
        }

        if (!Enum.IsDefined(options.BackgroundRemoval) ||
            !Enum.IsDefined(options.UvUnwrap) ||
            !Enum.IsDefined(options.Webp) ||
            !Enum.IsDefined(options.FlashAttention) ||
            !Enum.IsDefined(options.ModelFamily))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Unsupported enum value in generation options.");
        }

        if (options.FovDegrees is float fov && (!float.IsFinite(fov) || fov <= 0.0f || fov >= 180.0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options), fov, "FovDegrees must be finite and between 0 and 180 (exclusive).");
        }

        if (options.MeshScale is float meshScale && (!float.IsFinite(meshScale) || meshScale <= 0.0f))
        {
            throw new ArgumentOutOfRangeException(nameof(options), meshScale, "MeshScale must be finite and positive.");
        }

        if (options.ExtendPixel is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ExtendPixel, "ExtendPixel must be >= 0.");
        }
    }

    private static string FormatFloat(float value)
        => value.ToString("0.######", CultureInfo.InvariantCulture);
}
