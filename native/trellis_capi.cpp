// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonThreeD

// Thin C ABI lane over trellis.cpp: one exported entry that runs exactly what
// trellis-cli.exe's main() runs - trellis::parse_args() and then trellis_run().
// The dll never launches the executable; both are entry points into the same
// trellis_core, and this file is the exported one.
//
// Why argv rather than a parameter struct: trellis::TrellisParams is a ~50-field
// C++ struct with std::string members, and trellis.cpp exposes no C header at all
// (include/ is C++). Mirroring that surface in C would create a second copy of
// upstream's options that has to be kept in sync on every pull. The command line
// is the one contract that stays stable - trellis-cli parses it, trellis-server
// parses it per request - so exporting it verbatim is deliberate: no upstream
// source is modified, and SeasonThreeD reuses the same argument builder for both
// lanes.
//
// This file is compiled by a target that build-trellis.yml appends to
// trellis.cpp's own CMakeLists.txt, so it inherits that project's GGML, CUDA and
// MSVC runtime settings. It also compiles src/trellis_cli.cpp and
// src/trellis_args.cpp, because trellis_run() and parse_args() are part of the
// executables, not of the trellis_core static library.
//
// Contract: one call at a time per process. trellis_run() publishes the parsed
// knobs through globals (see the g_* declarations in trellis_args.h), so two
// concurrent calls in one process would race. SeasonThreeD serializes its calls for
// the worker lane for the same reason.
//
// Known limits of this lane, both inherited from upstream: output and progress
// go to the host process's stdout/stderr (there is no log sink to hook into),
// and trellis_run() has no cancellation hook, so a call runs to completion.

#include "trellis_args.h"
#include "trellis_run.h"

#include <cstdio>

#if defined(_WIN32)
#define TRELLIS_CAPI_EXPORT extern "C" __declspec(dllexport)
#else
#define TRELLIS_CAPI_EXPORT extern "C" __attribute__((visibility("default")))
#endif

// Generation of this lane's ABI. Bump only when the exported signature changes,
// so a caller can refuse a mismatched binary instead of guessing. Version 1 is
// "argv in, exit code out", deliberately the same semantics as the executable.
TRELLIS_CAPI_EXPORT int trellis_api_version(void) {
    return 1;
}

// Same body as src/trellis_cli_main.cpp: parse, then run. argv[0] is only used
// as the program name in the usage text, so a caller may pass any label.
// Returns 0 on success, 1 for a bad command line, else whatever trellis_run()
// reports (the same values the executable exits with).
//
// Deliberately does NOT touch stdio buffering - the exe calls setvbuf so
// redirection through a pipe stays live, but in a dll that would reconfigure the
// host's stream, and the log channel belongs to whoever loaded us.
TRELLIS_CAPI_EXPORT int trellis_run_argv(int argc, char** argv) {
    if (argc < 1 || argv == nullptr || argv[0] == nullptr) {
        return 1;
    }

    trellis::TrellisParams p;
    // parse_args() only reads argv (trellis_args.cpp), so the char** shape that
    // main() receives is enough here as well.
    if (!trellis::parse_args(argc, argv, p)) {
        trellis::print_usage(argv[0], /*server=*/false);
        return p.help ? 0 : 1;
    }
    if (p.image.empty()) {
        std::fprintf(stderr, "[trellis] no input image (give <image.png> or --image)\n");
        trellis::print_usage(argv[0], /*server=*/false);
        return 1;
    }

    return trellis_run(p);
}
