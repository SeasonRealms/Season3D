// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonThreeD

namespace Season.ThreeD;

/// <summary>
/// Finds the trellis worker. Unlike the other Season AI packages this is not a native
/// library next to the managed assembly only: the workflow publishes the executables
/// into <c>runtimes/&lt;rid&gt;/native</c>, MSBuild keeps that folder in a Windows output
/// (the worker brings its own ggml, see SeasonThreeD.csproj) and flattens the Linux one
/// into the output root, and a developer may keep a local build anywhere - so the search
/// covers all three, plus PATH.
///
/// Two lanes are published from one build (see
/// <c>.github/workflows/build-trellis.yml</c>) and the packaging step tells them apart by
/// file name, so the lane is a property of the file a caller selected rather than
/// something it has to remember: <c>trellis-cli</c> is the CUDA lane and
/// <c>trellis-cli-vulkan</c> the Vulkan one.
/// </summary>
internal static class TrellisRuntimeLocator
{
    internal const string EnvironmentVariable = "SEASONTHREED_TRELLIS_CLI";

    // The CUDA lane keeps the plain name so callers that predate the split keep working.
    internal const string PrimaryExecutableBaseName = "trellis-cli";
    internal const string VulkanExecutableBaseName = "trellis-cli-vulkan";

    /// <summary>
    /// Where the NVIDIA driver library lives on Linux. Its presence is what tells
    /// <see cref="TrellisWorker.Auto"/> that the CUDA lane is both usable and the faster
    /// choice, so the list covers the usual distro layouts rather than probing the
    /// loader.
    /// </summary>
    private static readonly string[] CudaDriverPaths =
    [
        "/usr/lib/x86_64-linux-gnu/libcuda.so.1",
        "/usr/lib/wsl/lib/libcuda.so.1",
        "/usr/lib64/libcuda.so.1",
        "/usr/lib/libcuda.so.1",
        "/usr/local/cuda/lib64/libcuda.so.1"
    ];

    /// <summary>
    /// Resolves the worker path, or throws naming every file it looked for, so the failure
    /// says which binary is missing rather than that "the worker" is.
    /// </summary>
    public static string Resolve(TrellisWorker worker, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException(
                    $"The trellis worker executable was not found at the configured path: {full}", full);
            }

            return full;
        }

        return TryResolve(worker, null) ?? throw new FileNotFoundException(DescribeMissingWorker(worker));
    }

    /// <summary>Resolves without throwing; <see langword="null"/> means "not installed".</summary>
    public static string? TryResolve(TrellisWorker worker, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var configured = Path.GetFullPath(explicitPath);
            return File.Exists(configured) ? configured : null;
        }

        // The override names one file, so it only answers for Auto: a caller that asked
        // for a specific lane gets that lane's binary or an honest "not installed".
        if (worker == TrellisWorker.Auto && TryResolveFromEnvironment() is { } fromEnvironment)
        {
            return fromEnvironment;
        }

        return TryResolveInLayouts(worker);
    }

    /// <summary>
    /// The lanes whose executable is present, in the order <see cref="TrellisWorker.Auto"/>
    /// prefers them. Lets a caller offer "CUDA or Vulkan" on a machine that has both
    /// without starting either.
    /// </summary>
    public static IReadOnlyList<TrellisWorker> AvailableWorkers()
    {
        TrellisWorker[] order = IsCudaDriverPresent()
            ? [TrellisWorker.Cuda, TrellisWorker.Vulkan]
            : [TrellisWorker.Vulkan, TrellisWorker.Cuda];

        var available = new List<TrellisWorker>(order.Length);
        foreach (var lane in order)
        {
            if (TryResolveInLayouts(lane) is not null)
            {
                available.Add(lane);
            }
        }

        return available;
    }

    /// <summary>
    /// The lane a resolved path belongs to. Read off the file name, because that is what
    /// Package() in build-trellis.yml renames - a custom path is classified the same way,
    /// which keeps "which lane am I running?" a string comparison rather than a probe.
    /// </summary>
    internal static TrellisWorker DetectLane(string executablePath)
        => Path.GetFileNameWithoutExtension(executablePath)
            .EndsWith("-vulkan", StringComparison.OrdinalIgnoreCase)
                ? TrellisWorker.Vulkan
                : TrellisWorker.Cuda;

    internal static bool IsCudaDriverPresent()
    {
        if (OperatingSystem.IsWindows())
        {
            return File.Exists(Path.Combine(Environment.SystemDirectory, "nvcuda.dll"));
        }

        if (OperatingSystem.IsLinux())
        {
            // libcuda is normally only on the dynamic loader's search path (ldconfig),
            // not on PATH, so probe the standard library directories instead.
            return CudaDriverPaths.Any(File.Exists);
        }

        // macOS only ever had the plain worker (Metal); no NVIDIA lane was shipped.
        return false;
    }

    private static string? TryResolveFromEnvironment()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return null;
        }

        try
        {
            var candidate = Path.GetFullPath(fromEnvironment);
            return File.Exists(candidate) ? candidate : null;
        }
        catch (ArgumentException)
        {
            // A malformed override is ignored rather than breaking discovery.
            return null;
        }
    }

    private static string? TryResolveInLayouts(TrellisWorker worker)
    {
        foreach (var directory in EnumerateSearchDirectories())
        {
            foreach (var name in EnumerateFileNames(worker))
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return ProbePath(worker);
    }

    private static string DescribeMissingWorker(TrellisWorker worker)
    {
        var expected = string.Join(
            " / ",
            EnumerateFileNames(worker).Distinct(StringComparer.OrdinalIgnoreCase));

        return $"{expected} was not found. Expected it next to the managed assembly, under " +
               $"runtimes/{GetRuntimeIdentifier()}/native, on PATH, or named by {EnvironmentVariable} " +
               $"(Auto only). Build it with .github/workflows/build-trellis.yml and copy the artifact " +
               "next to the app.";
    }

    /// <summary>
    /// The two layouts a consumer sees: the RID layout (the Windows output and the NuGet
    /// package) and the flat output root (the Linux Content items are linked by file
    /// name). The RID folder goes first: a worker there loads the ggml beside it, while a
    /// stale copy left in the root by an older build would load the app's.
    /// </summary>
    private static IEnumerable<string> EnumerateSearchDirectories()
    {
        var baseDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        yield return Path.Combine(baseDirectory, "runtimes", GetRuntimeIdentifier(), "native");
        yield return baseDirectory;
    }

    private static IEnumerable<string> EnumerateFileNames(TrellisWorker worker)
    {
        foreach (var baseName in EnumerateBaseNames(worker))
        {
            yield return OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
            // A Windows payload unpacked on a non-Windows host still resolves the managed
            // path but cannot run; keep the probe honest by only adding the bare name.
            if (OperatingSystem.IsWindows())
            {
                yield return baseName;
            }
        }
    }

    /// <summary>
    /// Candidate base names, most preferred first. Auto tries the lane that matches the
    /// machine and then the other one, so a package that staged a single lane still runs;
    /// an explicit lane tries only itself.
    /// </summary>
    private static IEnumerable<string> EnumerateBaseNames(TrellisWorker worker)
    {
        switch (worker)
        {
            case TrellisWorker.Cuda:
                yield return PrimaryExecutableBaseName;
                break;
            case TrellisWorker.Vulkan:
                yield return VulkanExecutableBaseName;
                break;
            default:
                if (IsCudaDriverPresent())
                {
                    yield return PrimaryExecutableBaseName;
                    yield return VulkanExecutableBaseName;
                }
                else
                {
                    yield return VulkanExecutableBaseName;
                    yield return PrimaryExecutableBaseName;
                }

                break;
        }
    }

    private static string? ProbePath(TrellisWorker worker)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in EnumerateFileNames(worker))
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), name);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is the environment's problem, not ours.
                }
            }
        }

        return null;
    }

    internal static string GetRuntimeIdentifier()
    {
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "unknown"
        };

        if (OperatingSystem.IsWindows())
        {
            return $"win-{architecture}";
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"osx-{architecture}";
        }

        return $"linux-{architecture}";
    }
}
