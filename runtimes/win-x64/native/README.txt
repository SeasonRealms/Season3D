trellis.cpp : 2afcbf49c2260f01c41f98c7d8820047c68b1a3d
SeasonThreeD: 0a875f6f6b3dc84d1a4af9545c013c62a8403bb4 (C ABI shim + apply-capi-lane.ps1)
ggml        : 7464e7958146b836495248b62a86b95c05273587 (SeasonRealms/ggml @ master)
ggml source : https://github.com/SeasonRealms/ggml - vendored into trellis.cpp thirdparty/ggml
ggml pin    : f8697b33795db2ee6a2569b40853ca6aaa69f9b9 (gitlink recorded by trellis.cpp)
lanes       : trellis-cli.exe / trellis-server.exe / trellis.dll = CUDA lane (GGML_CUDA=ON)
              trellis-cli-vulkan.exe / trellis-server-vulkan.exe / trellis-vulkan.dll
              = Vulkan lane (GGML_VULKAN=true, no CUDA)
              TrellisWorker.Auto runs the CUDA lane when nvcuda.dll is present, the
              Vulkan lane otherwise; both fall back to ggml-cpu-<variant>.dll.
configuration: Release
webp        : ON
GGML_NATIVE : OFF
GGML_OPENMP : OFF
ggml linkage: shared, GGML_BACKEND_DL=ON, GGML_CPU_ALL_VARIANTS=ON, GGML_MAX_NAME=128
ggml runtime: PACKAGED HERE - ggml.dll / ggml-base.dll / ggml-cpu*.dll /
              ggml-cuda.dll / ggml-vulkan.dll were compiled by this run from
              7464e7958146b836495248b62a86b95c05273587 and are interchangeable with the copies the other
              AI packages ship (same ggml_ref, same options), so no separate
              SeasonGGML build is required for this package to run
cuda runtime: NOT PACKAGED - cudart / cublas / cublasLt come from
              cudacudnn-runtime, which SeasonStudio stages next to the app
api lane    : thin C ABI - trellis.dll exports trellis_run_argv/trellis_api_version
              (parse_args+trellis_run, the same pair trellis-cli.exe runs)
cuda        : 12.8.1 (75-virtual;80-real;86-real;89-real;90-virtual)

Artifact contents:
ggml-base.dll
ggml-cpu-alderlake.dll
ggml-cpu-cannonlake.dll
ggml-cpu-cascadelake.dll
ggml-cpu-haswell.dll
ggml-cpu-icelake.dll
ggml-cpu-sandybridge.dll
ggml-cpu-skylakex.dll
ggml-cpu-sse42.dll
ggml-cpu-x64.dll
ggml-cuda.dll
ggml-vulkan.dll
ggml.dll
ggml.txt
trellis-cli-vulkan.exe
trellis-cli.exe
trellis-server-vulkan.exe
trellis-server.exe
trellis-vulkan.dll
trellis.dll
trellis.txt
README.txt
dependencies.txt
