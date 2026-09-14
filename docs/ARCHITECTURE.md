# Architecture

## Process boundary

Grasshopper and Rhino stay isolated from the native optimizer:

```text
Grasshopper UI thread
  capture inputs / publish outputs
        |
        v
solve-mode selection
  checked   -> GrasshopperAsyncComponent worker task
  unchecked -> synchronous UI-thread execution
  convert geometry / build previews
        |
        v
InstantMeshPlus.Core
  request.json + request.impbin
        |
        v
InstantMeshPlus.Worker
  .exe on Windows / extensionless on macOS
  pinned Instant Meshes solver
        |
        v
  result.json + result.impbin
        |
        v
Rhino mesh, full field result, previews, diagnostics
```

Every solve gets a unique temporary directory. JSON describes settings and
file names; binary payloads carry large float32 and uint32 arrays. This keeps
the protocol inspectable without turning geometry into large JSON documents.
The solver uses a .NET 8 source adaptation of Speckle's
GrasshopperAsyncComponent v2.0.3 worker pattern. Asynchronous solving is the
default. The component menu can switch the same captured input and worker
pipeline to synchronous UI-thread execution, and that choice is serialized
with the Grasshopper definition. In asynchronous mode, a new Grasshopper
solution eagerly cancels obsolete worker tasks, while a generation check
prevents stale tasks from publishing results or progress. The managed runner
owns the child process and kills its process tree on cancellation or timeout.

## Native pipeline

1. Validate settings and triangle indices.
2. Compute mesh statistics and derive target scale.
3. Subdivide an input that is too coarse for that scale.
4. Build directed-edge adjacency, normals, areas, hierarchy, and BVH.
5. Add boundary constraints and project user constraints/guides.
6. Propagate constraints through the multiresolution hierarchy.
7. Optimize the orientation field.
8. Detect and optionally move supported orientation singularities.
9. Optimize the position field.
10. Detect and optionally move supported position singularities.
11. Extract and optionally smooth/reproject the output mesh.
12. Return topology, field samples, singularities, counts, warnings, and
    timings.

The upstream source is linked into the worker only. Rhino SDK types never
cross the native boundary. The build keeps the pinned Instant Meshes submodule
unchanged and creates deterministic build-tree overlays for the legacy TBB
call sites. Both platforms use pinned oneTBB 2023.1.0. The Windows package
ships `tbb12.dll`; the macOS package ships a universal
`libtbb.12.dylib` and resolves it through `@loader_path`.

The macOS worker and oneTBB runtime are compiled independently for `arm64` and
`x86_64`, combined with `lipo`, and checked on native GitHub runners for both
architectures. Apple Developer ID signing and notarization can be enabled for
release hardening, but neither is required to compile, test, or package the
plug-in.

## Constraint semantics

- Orientation constraints are projected to a triangle, tangent-projected
  against each affected vertex normal, made symmetry-compatible, blended, and
  clamped to upstream constraint weights.
- Position constraints blend world-space anchors.
- Curve orientation uses the sampled curve tangent.
- Edge-path guides also anchor the position field to projected curve samples.
- Rejected projections and invalid local directions are counted instead of
  being silently accepted.
- Singularity movement searches for a detected singularity near the first
  curve point, projects the curve to faces, joins them through face adjacency,
  and invokes the upstream local edit.

## Extension points

- Add a protocol schema version before changing binary layout.
- Point-cloud input needs normal arrays and a separate adjacency branch.
- Staged solves need a persistent worker/session protocol rather than the
  current stateless process.
- Exact singularity prescription needs new mathematical constraints or
  topology edits in the native solver and should not be represented as
  ordinary field weights.
