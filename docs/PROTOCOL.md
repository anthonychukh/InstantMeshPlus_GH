# Worker protocol v1

All integers are little-endian. Geometry is float32. Indices and counts are
uint32 unless noted.

## Request manifest

`request.json` contains:

- `schema`: `1`;
- `payload`: request binary file name;
- `resultManifest`: worker result JSON file name;
- `resultPayload`: worker result binary file name;
- `settings`: target mode/value, RoSy, encoded PoSy, field/extraction feature
  flags, smoothing, deterministic mode, thread count, and projection tolerance.

Public 6-PoSy triangular mode is encoded as the upstream internal value `3`;
4-PoSy stays `4`.

## Request binary

```text
char[8]  "IMPREQ1\0"
uint32   schema
uint32   vertexCount
uint32   triangleCount
uint32   orientationConstraintCount
uint32   positionConstraintCount
uint32   guideCount
uint32   singularityPathCount
float32[vertexCount][3] vertices
uint32[triangleCount][3] triangles
(float32 point[3], direction[3], weight)[orientationConstraintCount]
(float32 point[3], anchor[3], weight)[positionConstraintCount]
(uint32 mode, float32 weight, uint32 count, float32 points[count][3])[guideCount]
(uint32 field, uint32 count, float32 points[count][3])[singularityPathCount]
```

Guide mode `0` is orientation and `1` is edge path. Field `0` is orientation
and `1` is position.

## Result manifest

The JSON status is either:

```json
{ "schema": 1, "status": "error", "message": "..." }
```

or `status: "ok"` with the payload file name, target scale, output counts,
constraint counts, warnings, and per-phase timings in milliseconds.

## Result binary

```text
char[8]  "IMPRES1\0"
uint32   schema
uint32   outputVertexCount
uint32   outputFaceArity
uint32   outputFaceCount
uint32   fieldSampleCount
uint32   orientationSingularityCount
uint32   positionSingularityCount
float32[outputVertexCount][3] vertices
uint32[outputFaceCount][outputFaceArity] faces
(float32 point[3], normal[3], orientation[3], position[3])[fieldSampleCount]
(uint32 face, int32 index, float32 point[3])[orientationSingularityCount]
(uint32 face, int32 indexU, int32 indexV, float32 point[3])[positionSingularityCount]
```

Readers enforce a maximum count of 200 million values and reject unknown
headers, schema versions, and face arities.
