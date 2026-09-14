set(INSTANT_MESHES_ROOT "${CMAKE_SOURCE_DIR}/third_party/instant-meshes")
set(ONETBB_ROOT "${CMAKE_SOURCE_DIR}/third_party/oneTBB")

if(NOT EXISTS "${INSTANT_MESHES_ROOT}/src/field.cpp")
  message(FATAL_ERROR "Instant Meshes submodule is missing. Run: git submodule update --init --recursive")
endif()
if(NOT EXISTS "${ONETBB_ROOT}/include/oneapi/tbb.h")
  message(FATAL_ERROR "oneTBB submodule is missing. Run: git submodule update --init --recursive")
endif()

set(BUILD_SHARED_LIBS ON CACHE BOOL "" FORCE)
set(TBB_TEST OFF CACHE BOOL "" FORCE)
set(TBB_EXAMPLES OFF CACHE BOOL "" FORCE)
set(TBBMALLOC_BUILD OFF CACHE BOOL "" FORCE)
set(TBBMALLOC_PROXY_BUILD OFF CACHE BOOL "" FORCE)
set(TBB_INSTALL OFF CACHE BOOL "" FORCE)
set(TBB_BUILD_APPLE_FRAMEWORKS OFF CACHE BOOL "" FORCE)
if(WIN32)
  set(TBB_VERIFY_DEPENDENCY_SIGNATURE ON CACHE BOOL "" FORCE)
endif()
add_subdirectory(
  "${ONETBB_ROOT}"
  "${CMAKE_BINARY_DIR}/third_party/oneTBB"
  EXCLUDE_FROM_ALL)
# Keep the private side-by-side runtime name identical in Debug and Release.
# oneTBB otherwise appends `_debug`, which breaks a portable Grasshopper
# payload and the macOS universal merge path.
set_target_properties(tbb PROPERTIES DEBUG_POSTFIX "")

include("${CMAKE_CURRENT_SOURCE_DIR}/cmake/PrepareInstantMeshesSources.cmake")
imp_prepare_instant_meshes_sources(
  "${INSTANT_MESHES_ROOT}/src"
  "${CMAKE_BINARY_DIR}/generated/instant-meshes")

add_library(instant_meshes_core STATIC
  "${INSTANT_MESHES_ROOT}/src/normal.cpp"
  "${INSTANT_MESHES_ROOT}/src/adjacency.cpp"
  "${INSTANT_MESHES_ROOT}/src/meshstats.cpp"
  "${INSTANT_MESHES_ROOT}/src/hierarchy.cpp"
  "${INSTANT_MESHES_ROOT}/src/extract.cpp"
  "${IMP_PATCHED_FIELD_SOURCE}"
  "${IMP_PATCHED_BVH_SOURCE}"
  "${INSTANT_MESHES_ROOT}/src/subdivide.cpp"
  "${INSTANT_MESHES_ROOT}/src/reorder.cpp"
  "${INSTANT_MESHES_ROOT}/src/cleanup.cpp"
  "${INSTANT_MESHES_ROOT}/src/dedge.cpp")

target_compile_features(instant_meshes_core PUBLIC cxx_std_17)
target_compile_definitions(instant_meshes_core PUBLIC
  EIGEN_MPL2_ONLY
  __TBB_NO_IMPLICIT_LINKAGE=1
  _CRT_SECURE_NO_WARNINGS)
if(MSVC)
  target_compile_definitions(instant_meshes_core PUBLIC
    _HAS_AUTO_PTR_ETC=1
    _SILENCE_CXX17_NEGATORS_DEPRECATION_WARNING
    _SILENCE_CXX17_ADAPTOR_TYPEDEFS_DEPRECATION_WARNING)
elseif(APPLE)
  target_compile_definitions(instant_meshes_core PUBLIC
    _LIBCPP_ENABLE_CXX17_REMOVED_BINDERS)
endif()
target_include_directories(instant_meshes_core PUBLIC
  "${CMAKE_CURRENT_SOURCE_DIR}/compat"
  "${INSTANT_MESHES_ROOT}/src"
  "${INSTANT_MESHES_ROOT}/ext/nanogui/ext/eigen"
  "${INSTANT_MESHES_ROOT}/ext/dset"
  "${INSTANT_MESHES_ROOT}/ext/pcg32")
target_link_libraries(instant_meshes_core PUBLIC TBB::tbb)

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
target_compile_features(InstantMeshPlus.Worker PRIVATE cxx_std_17)
target_include_directories(InstantMeshPlus.Worker PRIVATE
  "${CMAKE_SOURCE_DIR}/third_party/json/single_include")
target_link_libraries(InstantMeshPlus.Worker PRIVATE instant_meshes_core)

set_target_properties(InstantMeshPlus.Worker PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/worker")

if(WIN32)
  add_custom_command(TARGET InstantMeshPlus.Worker POST_BUILD
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different
      "$<TARGET_FILE:tbb>"
      "$<TARGET_FILE_DIR:InstantMeshPlus.Worker>/tbb12.dll"
    COMMENT "Copying the oneTBB runtime beside the worker")
elseif(APPLE)
  set_target_properties(InstantMeshPlus.Worker PROPERTIES
    BUILD_RPATH "@loader_path"
    BUILD_WITH_INSTALL_RPATH TRUE
    INSTALL_RPATH "@loader_path")
  add_custom_command(TARGET InstantMeshPlus.Worker POST_BUILD
    COMMAND "${CMAKE_COMMAND}" -E copy_if_different
      "$<TARGET_SONAME_FILE:tbb>"
      "$<TARGET_FILE_DIR:InstantMeshPlus.Worker>/libtbb.12.dylib"
    COMMENT "Copying the oneTBB runtime beside the worker")
endif()

add_test(NAME worker_help COMMAND InstantMeshPlus.Worker --help)
