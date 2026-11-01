// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonThreeD

// NativeLibrary.Load and the C runtime descriptor calls below are desktop-only, and this
// package still compiles for iOS / Mac Catalyst / Android (SeasonStudio references every
// AI package from all of its heads). Every entry point calls SeasonThreeD.EnsureSupported()
// first, so none of this can run there; the analyzer cannot see across that guard.
#pragma warning disable CA1416

namespace Season.ThreeD;

/// <summary>Everything one trellis run reported back.</summary>
internal sealed class TrellisRunOutcome
{
    public int ExitCode { get; init; }

    public uint Seed { get; init; }

    public TimeSpan Duration { get; init; }

    public IReadOnlyList<string> Log { get; init; } = [];

    /// <summary>The trailing <c>[trellis]</c> lines, used to explain a non-zero exit.</summary>
    public IReadOnlyList<string> ErrorLines { get; init; } = [];
}

/// <summary>
/// Runs one lane of the trellis C ABI in this process: <c>trellis_run_argv</c>, which is
/// the same <c>parse_args()</c> + <c>trellis_run()</c> pair the worker executable runs
/// (<c>native/trellis_capi.cpp</c>).
///
/// This is the only transport. The dll, not the exe, is what makes SeasonThreeD one
/// library-shaped API on every platform; the cost is that a native fault is no longer
/// isolated by a process boundary, which is accepted because every AI package in the
/// product loads native code in-process already.
///
/// Two properties of the shim shape the code below:
/// <list type="bullet">
/// <item>one call per process at a time - <c>trellis_run()</c> publishes the parsed knobs
/// through globals, so <see cref="s_gate"/> serializes every invocation; it also
/// serializes the descriptor redirection, which is process-wide;</item>
/// <item>no log sink and no cancel hook - output goes to the host's stdout/stderr, so the
/// run is captured by temporarily pointing descriptors 1 and 2 at a pipe of our own, and
/// a call always runs to completion (cancellation and the run budget are reported after
/// the fact).</item>
/// </list>
/// </summary>
internal static class TrellisNativeRunner
{
    // A capped log keeps a 1536-cascade verbose run (thousands of per-step lines) from
    // pinning memory in the UI; the tail is what diagnostics needs anyway.
    private const int MaximumLogLines = 20_000;

    private static readonly SemaphoreSlim s_gate = new(1, 1);

    public static async Task<TrellisRunOutcome> RunAsync(
        string libraryPath,
        IReadOnlyList<string> arguments,
        IEnumerable<KeyValuePair<string, string>>? environmentVariables,
        TimeSpan? timeout,
        Action<TrellisLogLevel, string>? logCallback,
        IProgress<TrellisProgress>? progress,
        CancellationToken cancellationToken)
    {
        ThrowIfCancellationRequested(cancellationToken);

        // Resolve and load before taking the gate: a missing dependency or a wrong ABI
        // should fail now, not behind a run that takes tens of minutes.
        var library = TrellisNativeLibrary.Load(libraryPath);

        await s_gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        EnvironmentSnapshot? environment = null;
        try
        {
            environment = EnvironmentSnapshot.Apply(environmentVariables);

            var parser = new TrellisOutputParser();
            var log = new List<string>();
            var logLock = new object();
            var stopwatch = Stopwatch.StartNew();

            void OnLine(string line, bool isStandardError)
            {
                TrellisProgressUpdate update = default;
                bool hasUpdate;

                lock (logLock)
                {
                    if (log.Count < MaximumLogLines)
                    {
                        log.Add(line);
                    }

                    hasUpdate = parser.TryParse(line, out update);
                }

                logCallback?.Invoke(
                    isStandardError ? TrellisLogLevel.Warning : TrellisLogLevel.Info,
                    line);

                if (hasUpdate)
                {
                    progress?.Report(TrellisProgress.FromUpdate(update, stopwatch.Elapsed));
                }
            }

            int exitCode;

            // The redirection below takes ownership of descriptors 1 and 2, which also
            // closes the Win32 handles behind them - and .NET's Console cached those very
            // handles. Detaching Console first keeps it from writing into a dead handle
            // while the run is in flight; ConsoleProxy.Resume() rebuilds it afterwards.
            var console = ConsoleProxy.Suspend();
            try
            {
                using (var stdout = NativeFdCapture.TryStart(standardOutput: true, OnLine))
                using (var stderr = NativeFdCapture.TryStart(standardOutput: false, OnLine))
                {
                    if (stdout is null && stderr is null)
                    {
                        logCallback?.Invoke(
                            TrellisLogLevel.Warning,
                            "[season] the host has neither a stdout nor a stderr descriptor; " +
                            "the trellis log will be empty (the run itself is unaffected)");
                    }

                    exitCode = await Task.Run(() => library.Invoke(arguments)).ConfigureAwait(false);

                    // Flush whatever the native side still holds in its block buffer, then
                    // let the readers see EOF before the descriptors are handed back.
                    stdout?.Complete();
                    stderr?.Complete();
                }
            }
            finally
            {
                console?.Resume();
            }

            stopwatch.Stop();

            IReadOnlyList<string> errorLines;
            lock (logLock)
            {
                errorLines = log
                    .Where(static line => line.TrimStart().StartsWith("[trellis]", StringComparison.Ordinal))
                    .TakeLast(8)
                    .ToArray();
            }

            var outcome = new TrellisRunOutcome
            {
                ExitCode = exitCode,
                Seed = parser.Seed,
                Duration = stopwatch.Elapsed,
                Log = log.ToArray(),
                ErrorLines = errorLines
            };

            // Both are reported only now: trellis_run() has no cancellation hook and no
            // deadline, so the honest contract is "the run finished, and it overran".
            ThrowIfCancellationRequested(cancellationToken);

            if (timeout is { } budget && outcome.Duration > budget)
            {
                throw new TimeoutException(
                    $"trellis ran for {outcome.Duration.TotalMinutes:0.#} minutes, past the " +
                    $"{budget.TotalMinutes:0.#}-minute budget. The native lane cannot be interrupted; " +
                    "the run completed and its log is in the callback.");
            }

            return outcome;
        }
        finally
        {
            environment?.Restore();
            s_gate.Release();
        }
    }

    private static void ThrowIfCancellationRequested(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "The trellis native lane cannot be interrupted once trellis_run_argv() has been " +
                "entered; cancellation is observed before the call and after it returns.",
                cancellationToken);
        }
    }
}

/// <summary>
/// One loaded <c>trellis.dll</c> / <c>trellis-vulkan.dll</c>. Images are keyed by full path
/// and never unloaded: the loader deduplicates by path, and a second lane is a second
/// image with its own copy of trellis_core (each statically links the kernels it was built
/// for) while ggml stays the single shared module the rest of the process uses.
/// </summary>
internal sealed class TrellisNativeLibrary
{
    /// <summary>The shim's generation of "argv in, exit code out".</summary>
    private const int ExpectedApiVersion = 1;

    // What trellis.dll imports besides the OS. ggml must come from the same folder the rest
    // of the process uses, and cudart is what the CUDA lane's own kernels link; loading them
    // by full path first is what keeps the loader from resolving either from somewhere else
    // on PATH. Anything absent is left to the import resolver, which then reports it by name.
    private static readonly string[] s_siblingDependencies =
    [
        "ggml.dll",
        "ggml-base.dll",
        "cudart64_12.dll",
        "cublas64_12.dll",
        "cublasLt64_12.dll"
    ];

    private static readonly Dictionary<string, TrellisNativeLibrary> s_loaded =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object s_loadedLock = new();

    private readonly string _path;
    private readonly RunArgv _runArgv;

    private TrellisNativeLibrary(string path, RunArgv runArgv)
    {
        _path = path;
        _runArgv = runArgv;
    }

    private delegate int ApiVersion();

    private delegate int RunArgv(int argc, IntPtr argv);

    public static TrellisNativeLibrary Load(string libraryPath)
    {
        var fullPath = Path.GetFullPath(libraryPath);

        lock (s_loadedLock)
        {
            if (s_loaded.TryGetValue(fullPath, out var cached))
            {
                return cached;
            }

            PreloadSiblings(fullPath);

            IntPtr handle;
            try
            {
                handle = NativeLibrary.Load(fullPath);
            }
            catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
            {
                throw new InvalidOperationException(
                    $"The trellis native library could not be loaded: {fullPath}. " +
                    "Its imports are ggml.dll / ggml-base.dll plus, on the CUDA lane, " +
                    $"cudart64_12.dll (cudacudnn-runtime), and ggml's backend modules must sit " +
                    $"next to the application. {exception.Message}",
                    exception);
            }

            var versionExport = GetExport(handle, fullPath, "trellis_api_version");
            var version = Marshal.GetDelegateForFunctionPointer<ApiVersion>(versionExport)();
            if (version != ExpectedApiVersion)
            {
                throw new InvalidOperationException(
                    $"The trellis native library at {fullPath} speaks C ABI version {version}; " +
                    $"SeasonThreeD binds version {ExpectedApiVersion}. Rebuild it with this " +
                    "repository's native/trellis_capi.cpp.");
            }

            var runExport = GetExport(handle, fullPath, "trellis_run_argv");
            var library = new TrellisNativeLibrary(
                fullPath,
                Marshal.GetDelegateForFunctionPointer<RunArgv>(runExport));

            s_loaded[fullPath] = library;
            return library;
        }
    }

    /// <summary>One call. The caller holds the process-wide gate.</summary>
    public int Invoke(IReadOnlyList<string> arguments)
    {
        // argv[0] is only the program name in the usage text, so the file name is enough.
        var argc = arguments.Count + 1;
        var argv = Marshal.AllocHGlobal(IntPtr.Size * (argc + 1));
        var strings = new IntPtr[argc];

        try
        {
            strings[0] = Marshal.StringToCoTaskMemUTF8(Path.GetFileName(_path));
            Marshal.WriteIntPtr(argv, 0, strings[0]);

            for (var i = 0; i < arguments.Count; i++)
            {
                strings[i + 1] = Marshal.StringToCoTaskMemUTF8(arguments[i]);
                Marshal.WriteIntPtr(argv, IntPtr.Size * (i + 1), strings[i + 1]);
            }

            Marshal.WriteIntPtr(argv, IntPtr.Size * argc, IntPtr.Zero);
            return _runArgv(argc, argv);
        }
        finally
        {
            foreach (var pointer in strings)
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }

            Marshal.FreeHGlobal(argv);
        }
    }

    private static IntPtr GetExport(IntPtr handle, string path, string name)
    {
        if (!NativeLibrary.TryGetExport(handle, name, out var export))
        {
            throw new InvalidOperationException(
                $"The trellis native library at {path} does not export {name}. " +
                "It is not the C ABI lane this package binds (see native/trellis_capi.cpp).");
        }

        return export;
    }

    private static void PreloadSiblings(string libraryPath)
    {
        var directory = Path.GetDirectoryName(libraryPath);
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

        foreach (var name in s_siblingDependencies)
        {
            var candidate = Path.Combine(directory, name);
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                // Already loaded resolves to the loaded image; a failure here is retried by
                // the import resolver, which produces the message that names the file.
                NativeLibrary.Load(candidate);
            }
            catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException)
            {
            }
        }
    }
}

/// <summary>
/// Redirects a C runtime descriptor (1 or 2) into a pipe for the duration of one call and
/// turns whatever the native side writes into lines.
///
/// Pointing a descriptor at the pipe closes the Win32 handle it used to hold, so the host's
/// console has to be detached for the duration as well (see <see cref="ConsoleProxy"/>). The
/// capture is started after the gate is taken because the redirection is process-wide, and a
/// concurrent native library must not have its stderr stolen.
/// </summary>
internal sealed class NativeFdCapture : IDisposable
{
    private const int ReaderJoinMilliseconds = 2_000;

    private readonly int _readFd;
    private readonly int _savedFd;
    private readonly int _targetFd;
    private readonly bool _isStandardError;
    private readonly Action<string, bool> _onLine;
    private readonly Thread _reader;
    private int _completed;

    private NativeFdCapture(
        int readFd,
        int savedFd,
        int targetFd,
        bool isStandardError,
        Action<string, bool> onLine)
    {
        _readFd = readFd;
        _savedFd = savedFd;
        _targetFd = targetFd;
        _isStandardError = isStandardError;
        _onLine = onLine;

        _reader = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = isStandardError ? "trellis stderr" : "trellis stdout"
        };
        _reader.Start();
    }

    /// <summary>Starts capturing, or null when the host has no usable descriptor.</summary>
    public static NativeFdCapture? TryStart(bool standardOutput, Action<string, bool> onLine)
    {
        var target = standardOutput ? 1 : 2;

        try
        {
            var saved = NativePlatform.Dup(target);
            if (saved < 0)
            {
                return null;
            }

            var fds = new int[2];
            if (NativePlatform.CreatePipe(fds) != 0)
            {
                NativePlatform.Close(saved);
                return null;
            }

            if (NativePlatform.Dup2(fds[1], target) < 0)
            {
                NativePlatform.Close(fds[0]);
                NativePlatform.Close(fds[1]);
                NativePlatform.Close(saved);
                return null;
            }

            // The duplicated descriptor keeps the pipe open; our own copy is surplus.
            NativePlatform.Close(fds[1]);

            return new NativeFdCapture(fds[0], saved, target, !standardOutput, onLine);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Flushes, hands the descriptor back, and lets the reader drain to EOF.</summary>
    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        try
        {
            // A block-buffered stdout still holds its tail; without this the last lines of
            // a failed run are exactly the ones lost.
            NativePlatform.FlushAll();
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
        }

        NativePlatform.Dup2(_savedFd, _targetFd);
        NativePlatform.Close(_savedFd);

        // EOF arrives once the target descriptor no longer references the pipe; the join is
        // bounded because a native thread that kept a descriptor alive would otherwise hang
        // the caller on a run that already finished.
        _reader.Join(ReaderJoinMilliseconds);
        NativePlatform.Close(_readFd);
    }

    public void Dispose() => Complete();

    private void ReadLoop()
    {
        var buffer = new byte[8192];
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        var line = new StringBuilder(256);

        while (true)
        {
            var read = NativePlatform.Read(_readFd, buffer, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            var count = decoder.GetChars(buffer, 0, read, chars, 0);
            for (var i = 0; i < count; i++)
            {
                var character = chars[i];
                if (character == '\n')
                {
                    Emit(line.ToString());
                    line.Clear();
                }
                else if (character != '\r')
                {
                    line.Append(character);
                }
            }
        }

        if (line.Length > 0)
        {
            Emit(line.ToString());
        }
    }

    private void Emit(string line)
    {
        try
        {
            _onLine(line, _isStandardError);
        }
        catch (Exception)
        {
            // The sink is caller code (a log callback, a progress handler). One throwing
            // handler must not stop the pump and leave the run without its tail.
        }
    }
}

/// <summary>
/// The UCRT / libc descriptor calls shared by <see cref="NativeFdCapture"/> and
/// <see cref="ConsoleProxy"/>, kept in one place so the two cannot drift apart on what
/// "descriptor 1" means.
/// </summary>
internal static class NativePlatform
{
    private const int TextModeBinary = 0x8000;
    private const int PipeBufferSize = 0x4000;

    public static int CreatePipe(int[] fds)
    {
        // The two descriptors are written by the callee, so the array has to be
        // marshalled through a pointer: a plain int[] is [In] by default and would
        // come back as the zeroes we passed in - closing fd 0 instead of the pipe.
        unsafe
        {
            fixed (int* pointer = fds)
            {
                return OperatingSystem.IsWindows()
                    ? Ucrt.Pipe(pointer, PipeBufferSize, TextModeBinary)
                    : Libc.Pipe(pointer);
            }
        }
    }

    public static int Dup(int fd)
        => OperatingSystem.IsWindows() ? Ucrt.Dup(fd) : Libc.Dup(fd);

    public static int Dup2(int source, int target)
        => OperatingSystem.IsWindows() ? Ucrt.Dup2(source, target) : Libc.Dup2(source, target);

    public static int Read(int fd, byte[] buffer, int count)
    {
        if (OperatingSystem.IsWindows())
        {
            unsafe
            {
                fixed (byte* pointer = buffer)
                {
                    return Ucrt.Read(fd, pointer, (uint)count);
                }
            }
        }

        unsafe
        {
            fixed (byte* pointer = buffer)
            {
                return (int)Libc.Read(fd, pointer, (nuint)count);
            }
        }
    }

    public static void Close(int fd)
    {
        if (OperatingSystem.IsWindows())
        {
            Ucrt.Close(fd);
        }
        else
        {
            Libc.Close(fd);
        }
    }

    public static void FlushAll()
    {
        if (OperatingSystem.IsWindows())
        {
            Ucrt.Fflush(IntPtr.Zero);
        }
        else
        {
            Libc.Fflush(IntPtr.Zero);
        }
    }

    /// <summary>
    /// Points a process std handle (STD_OUTPUT_HANDLE / STD_ERROR_HANDLE) at the OS handle
    /// that currently backs the given descriptor, so <c>Console.OpenStandardOutput()</c>
    /// finds a live handle again after a redirection was undone.
    /// </summary>
    public static void AdoptDescriptorHandle(int which, int fd)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = Ucrt.GetOsFileHandle(fd);
        if (handle != IntPtr.Zero && handle != InvalidHandleValue)
        {
            Kernel32.SetStdHandle(which, handle);
        }
    }

    private static readonly IntPtr InvalidHandleValue = new(-1);

    private static class Ucrt
    {
        private const string Library = "ucrtbase.dll";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_pipe")]
        internal static extern unsafe int Pipe(int* fds, uint size, int textMode);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_dup")]
        internal static extern int Dup(int fd);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_dup2")]
        internal static extern int Dup2(int source, int target);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_read")]
        internal static extern unsafe int Read(int fd, byte* buffer, uint count);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_close")]
        internal static extern int Close(int fd);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Fflush(IntPtr stream);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "_get_osfhandle")]
        internal static extern IntPtr GetOsFileHandle(int fd);
    }

    private static class Kernel32
    {
        private const string Library = "kernel32.dll";

        [DllImport(Library, SetLastError = true)]
        internal static extern bool SetStdHandle(int which, IntPtr handle);
    }

    private static class Libc
    {
        private const string Library = "libc";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe int Pipe(int* fds);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Dup(int fd);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Dup2(int source, int target);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern unsafe nint Read(int fd, byte* buffer, nuint count);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Close(int fd);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Fflush(IntPtr stream);
    }
}

/// <summary>
/// Detaches <see cref="Console"/> from the descriptors <see cref="NativeFdCapture"/> takes
/// over, and rebuilds it once they are handed back.
///
/// <c>_dup2(pipe, 1)</c> closes the Win32 handle that backed descriptor 1, and .NET's console
/// writer had cached that very handle: the next <c>Console.WriteLine</c> throws "The handle is
/// invalid", and undoing the redirection does not revive the closed value. Swapping in
/// <see cref="TextWriter.Null"/> keeps the stale writer out of the way while the run is in
/// flight; resuming re-points the process std handles at the restored descriptors and builds
/// fresh writers over them. Console output written during a run is dropped - the native call
/// owns the console for its duration.
/// </summary>
internal sealed class ConsoleProxy
{
    private const int StandardOutputHandle = -11;
    private const int StandardErrorHandle = -12;

    private readonly Encoding _encoding;

    private ConsoleProxy(Encoding encoding) => _encoding = WithoutPreamble(encoding);

    /// <summary>Silences the console, or null when it could not be taken over.</summary>
    public static ConsoleProxy? Suspend()
    {
        try
        {
            var proxy = new ConsoleProxy(Console.OutputEncoding);
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            return proxy;
        }
        catch (Exception exception) when (IsConsoleFailure(exception))
        {
            return null;
        }
    }

    /// <summary>Rebuilds the console over whatever the descriptors hold now.</summary>
    public void Resume()
    {
        try
        {
            NativePlatform.AdoptDescriptorHandle(StandardOutputHandle, 1);
            NativePlatform.AdoptDescriptorHandle(StandardErrorHandle, 2);

            // Deliberately not the saved writers: they wrap the handle that the redirection
            // closed. Only a fresh stream over the restored handle works afterwards.
            Console.SetOut(Wrap(Console.OpenStandardOutput()));
            Console.SetError(Wrap(Console.OpenStandardError()));
        }
        catch (Exception exception) when (IsConsoleFailure(exception))
        {
            // A silent console beats one that throws on every subsequent write.
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }
    }

    private StreamWriter Wrap(Stream stream)
        => new(stream, _encoding, bufferSize: 1024, leaveOpen: false) { AutoFlush = true };

    private static bool IsConsoleFailure(Exception exception)
        => exception is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or PlatformNotSupportedException;

    private static Encoding WithoutPreamble(Encoding encoding) => encoding.CodePage switch
    {
        // The console builds its own writer from a preamble-free encoding, so anything that
        // writes into the same stream has to do the same: a StreamWriter writes the preamble
        // on its first flush, which would drop a stray BOM in the middle of the output.
        65001 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        1200 => new UnicodeEncoding(bigEndian: false, byteOrderMark: false),
        1201 => new UnicodeEncoding(bigEndian: true, byteOrderMark: false),
        12000 => new UTF32Encoding(bigEndian: false, byteOrderMark: false),
        12001 => new UTF32Encoding(bigEndian: true, byteOrderMark: false),
        _ => encoding
    };
}

/// <summary>
/// Applies <see cref="TrellisSessionOptions.EnvironmentVariables"/> for one run and puts
/// the previous values back afterwards: the switches are process-wide, so they must not
/// outlive the call that asked for them.
/// </summary>
internal sealed class EnvironmentSnapshot
{
    private readonly List<(string Name, string? Previous)> _entries = [];

    public static EnvironmentSnapshot? Apply(IEnumerable<KeyValuePair<string, string>>? variables)
    {
        if (variables is null)
        {
            return null;
        }

        var snapshot = new EnvironmentSnapshot();
        foreach (var pair in variables)
        {
            snapshot._entries.Add((pair.Key, Environment.GetEnvironmentVariable(pair.Key)));
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }

        return snapshot;
    }

    public void Restore()
    {
        foreach (var (name, previous) in _entries)
        {
            Environment.SetEnvironmentVariable(name, previous);
        }

        _entries.Clear();
    }
}

#pragma warning restore CA1416
