set(INSTANT_MESHES_ROOT "${CMAKE_SOURCE_DIR}/third_party/instant-meshes")

if(NOT EXISTS "${INSTANT_MESHES_ROOT}/src/field.cpp")
  message(FATAL_ERROR "Instant Meshes submodule is missing. Run: git submodule update --init --recursive")
endif()

set(TBB_BUILD_STATIC ON CACHE BOOL "" FORCE)
set(TBB_BUILD_SHARED OFF CACHE BOOL "" FORCE)
set(TBB_BUILD_TBBMALLOC OFF CACHE BOOL "" FORCE)
set(TBB_BUILD_TBBMALLOC_PROXY OFF CACHE BOOL "" FORCE)
set(TBB_BUILD_TESTS OFF CACHE BOOL "" FORCE)
add_subdirectory(
  "${INSTANT_MESHES_ROOT}/ext/tbb"
  "${CMAKE_BINARY_DIR}/third_party/tbb"
  EXCLUDE_FROM_ALL)

add_library(instant_meshes_core STATIC
  "${INSTANT_MESHES_ROOT}/src/normal.cpp"
  "${INSTANT_MESHES_ROOT}/src/adjacency.cpp"
  "${INSTANT_MESHES_ROOT}/src/meshstats.cpp"
  "${INSTANT_MESHES_ROOT}/src/hierarchy.cpp"
  "${INSTANT_MESHES_ROOT}/src/extract.cpp"
  "${INSTANT_MESHES_ROOT}/src/field.cpp"
  "${INSTANT_MESHES_ROOT}/src/bvh.cpp"
  "${INSTANT_MESHES_ROOT}/src/subdivide.cpp"
  "${INSTANT_MESHES_ROOT}/src/reorder.cpp"
  "${INSTANT_MESHES_ROOT}/src/cleanup.cpp"
  "${INSTANT_MESHES_ROOT}/src/dedge.cpp")

target_compile_features(instant_meshes_core PUBLIC cxx_std_14)
target_compile_definitions(instant_meshes_core PUBLIC
  EIGEN_MPL2_ONLY
  _CRT_SECURE_NO_WARNINGS
  __TBB_NO_IMPLICIT_LINKAGE)
target_include_directories(instant_meshes_core PUBLIC
  "${INSTANT_MESHES_ROOT}/src"
  "${INSTANT_MESHES_ROOT}/ext/nanogui/ext/eigen"
  "${INSTANT_MESHES_ROOT}/ext/tbb/include"
  "${INSTANT_MESHES_ROOT}/ext/dset"
  "${INSTANT_MESHES_ROOT}/ext/pss"
  "${INSTANT_MESHES_ROOT}/ext/pcg32")
target_link_libraries(instant_meshes_core PUBLIC tbb_static)

if(MSVC)
  target_compile_options(instant_meshes_core PRIVATE
    /W3 /FS
    /wd4018 /wd4100 /wd4127 /wd4146 /wd4244 /wd4267 /wd4503 /wd4510
    /wd4512 /wd4610 /wd4706 /wd4800)
endif()

add_executable(InstantMeshPlus.Worker
  src/Engine.cpp
  src/Engine.h
  src/Protocol.cpp
  src/Protocol.h
  src/Types.h
  src/main.cpp)
target_compile_features(InstantMeshPlus.Worker PRIVATE cxx_std_14)
target_include_directories(InstantMeshPlus.Worker PRIVATE
  "${CMAKE_SOURCE_DIR}/third_party/json/single_include")
target_compile_definitions(InstantMeshPlus.Worker PRIVATE
  _SILENCE_EXPERIMENTAL_FILESYSTEM_DEPRECATION_WARNING)
target_link_libraries(InstantMeshPlus.Worker PRIVATE instant_meshes_core)

set_target_properties(InstantMeshPlus.Worker PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/worker")

add_test(NAME worker_help COMMAND InstantMeshPlus.Worker --help)
