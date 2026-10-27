// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/Season3D

namespace Season.ThreeD;

/// <summary>
/// Finds the trellis-cli worker. Unlike the other Season AI packages this is not a
/// native library next to the managed assembly only: the workflow publishes the
/// executables into <c>runtimes/&lt;rid&gt;/native</c>, MSBuild flattens them into the
/// output root, and a developer may keep a local build anywhere - so the search covers
/// all three, plus PATH.
/// </summary>
internal static class TrellisRuntimeLocator
{
    internal const string EnvironmentVariable = "SEASON3D_TRELLIS_CLI";
    internal const string ExecutableBaseName = "trellis-cli";

    /// <summary>
    /// Resolves the worker path, or throws with the directories that were probed so the
    /// failure tells the user which file to drop where.
    /// </summary>
    public static string Resolve(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            if (!File.Exists(full))
            {
                throw new FileNotFoundException(
                    $"The trellis-cli executable was not found at the configured path: {full}", full);
            }

            return full;
        }

        return TryResolve(null)
               ?? throw new FileNotFoundException(
                   "trellis-cli was not found. Expected it next to the managed assembly, under " +
                   $"runtimes/{GetRuntimeIdentifier()}/native, on PATH, or named by {EnvironmentVariable}. " +
                   "Build it with .github/workflows/build-trellis.yml and copy the artifact next to the app.");
    }

    /// <summary>Resolves without throwing; <see langword="null"/> means "not installed".</summary>
    public static string? TryResolve(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var configured = Path.GetFullPath(explicitPath);
            return File.Exists(configured) ? configured : null;
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            try
            {
                var candidate = Path.GetFullPath(fromEnvironment);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed override is ignored rather than breaking discovery.
            }
        }

        foreach (var directory in EnumerateSearchDirectories())
        {
            foreach (var name in EnumerateFileNames())
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return ProbePath();
    }

    /// <summary>
    /// The two layouts a consumer sees: the flat output root (Content items are linked
    /// by file name) and the RID layout preserved inside the NuGet package.
    /// </summary>
    private static IEnumerable<string> EnumerateSearchDirectories()
    {
        var baseDirectory = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        yield return baseDirectory;
        yield return Path.Combine(baseDirectory, "runtimes", GetRuntimeIdentifier(), "native");
    }

    private static IEnumerable<string> EnumerateFileNames()
    {
        yield return OperatingSystem.IsWindows() ? ExecutableBaseName + ".exe" : ExecutableBaseName;
        // A Windows payload unpacked on a non-Windows host still resolves the managed
        // path but cannot run; keep the probe honest by only adding the bare name.
        if (OperatingSystem.IsWindows())
        {
            yield return ExecutableBaseName;
        }
    }

    private static string? ProbePath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in EnumerateFileNames())
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
