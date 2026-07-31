# Architecture

## Process boundary

Grasshopper and Rhino stay isolated from the native optimizer:

```text
Grasshopper UI thread
  capture inputs / publish outputs
        |
        v
GrasshopperAsyncComponent worker task
  convert geometry / build previews
        |
        v
InstantMeshPlus.Core
  request.json + request.impbin
        |
        v
InstantMeshPlus.Worker.exe
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
GrasshopperAsyncComponent v2.0.3 worker pattern. A new Grasshopper solution
eagerly cancels obsolete worker tasks, while a generation check prevents stale
tasks from publishing results or progress. Mesh conversion, native execution,
and preview construction run off the Rhino UI thread. The managed runner owns
the child process and kills its process tree on cancellation or timeout.

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
cross the native boundary.

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
