trellis.cpp : 2afcbf49c2260f01c41f98c7d8820047c68b1a3d
SeasonThreeD: 6992926edba187dee6eab70d1f31578390dbaa52 (C ABI shim + apply-capi-lane.ps1)
ggml        : 30859950c3f13f146979561e523028737b0f5e38 (SeasonRealms/ggml @ master)
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
ggml linkage: shared, GGML_BACKEND_DL=ON, GGML_CPU_ALL_VARIANTS=ON, GGML_MAX_NAME=160
ggml runtime: NOT PACKAGED - build-ggml.yml (SeasonGGML, artifact
              ggml-win-x64-multi-backend-*) is the single download source for
              ggml.dll / ggml-base.dll / ggml-cpu*.dll / ggml-cuda.dll /
              ggml-vulkan.dll (same ggml_ref, same options). Stage that artifact
              next to this one; the workers and trellis*.dll resolve it by name.
              This run still compiled 30859950c3f13f146979561e523028737b0f5e38 from the same tree with the
              same options, but that copy only feeds the pre-upload verification
cuda runtime: NOT PACKAGED - cudart / cublas / cublasLt come from
              cudacudnn-runtime, which SeasonStudio stages next to the app
api lane    : thin C ABI - trellis.dll exports trellis_run_argv/trellis_api_version
              (parse_args+trellis_run, the same pair trellis-cli.exe runs)
cuda        : 12.8.1 (75-virtual;80-real;86-real;89-real;90-virtual)

Artifact contents:
trellis-cli-vulkan.exe
trellis-cli.exe
trellis-server-vulkan.exe
trellis-server.exe
trellis-vulkan.dll
trellis.dll
trellis.txt
README.txt
dependencies.txt
dependencies.txt was computed against a staging folder that also held the ggml runtime (build-ggml.yml's artifact) and cudart / cublas / cublasLt (cudacudnn-runtime), i.e. the layout SeasonStudio produces; rows marked 'package' therefore include the two runtimes this artifact does not ship.
