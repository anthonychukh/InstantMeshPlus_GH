#pragma once

#include <array>
#include <cstdint>
#include <map>
#include <string>
#include <vector>

namespace imp {

using Vec3 = std::array<float, 3>;
using Tri = std::array<std::uint32_t, 3>;

enum class TargetMode {
    FaceCount,
    VertexCount,
    EdgeLength
};

enum class GuideMode : std::uint32_t {
    Orientation = 0,
    EdgePath = 1
};

enum class FieldKind : std::uint32_t {
    Orientation = 0,
    Position = 1
};

struct Settings {
    TargetMode targetMode = TargetMode::FaceCount;
    float targetValue = 1000.0f;
    int rosy = 4;
    int posy = 4;
    bool extrinsic = true;
    bool alignBoundaries = true;
    float creaseAngleDegrees = 90.0f;
    int smoothIterations = 2;
    bool pureOutput = true;
    bool deterministic = false;
    int threadCount = -1;
    float projectionTolerance = 0.0f;
};

struct OrientationConstraint {
    Vec3 point;
    Vec3 direction;
    float weight = 1.0f;
};

struct PositionConstraint {
    Vec3 point;
    Vec3 anchor;
    float weight = 1.0f;
};

struct CurveGuide {
    GuideMode mode = GuideMode::Orientation;
    float weight = 1.0f;
    std::vector<Vec3> points;
};

struct SingularityPath {
    FieldKind field = FieldKind::Orientation;
    std::vector<Vec3> points;
};

struct Request {
    std::string manifestPath;
    std::string resultManifestPath;
    std::string resultPayloadPath;
    Settings settings;
    std::vector<Vec3> vertices;
    std::vector<Tri> triangles;
    std::vector<OrientationConstraint> orientationConstraints;
    std::vector<PositionConstraint> positionConstraints;
    std::vector<CurveGuide> guides;
    std::vector<SingularityPath> singularityPaths;
};

struct FieldSample {
    Vec3 point;
    Vec3 normal;
    Vec3 orientation;
    Vec3 position;
};

struct OrientationSingularity {
    std::uint32_t face = 0;
    std::int32_t index = 0;
    Vec3 point;
};

struct PositionSingularity {
    std::uint32_t face = 0;
    std::int32_t indexU = 0;
    std::int32_t indexV = 0;
    Vec3 point;
};

struct Result {
    float scale = 0.0f;
    std::uint32_t outputFaceArity = 3;
    std::vector<Vec3> outputVertices;
    std::vector<std::uint32_t> outputIndices;
    std::vector<FieldSample> field;
    std::vector<OrientationSingularity> orientationSingularities;
    std::vector<PositionSingularity> positionSingularities;
    std::vector<std::string> warnings;
    std::map<std::string, double> timingsMilliseconds;
    std::size_t appliedOrientationConstraints = 0;
    std::size_t appliedPositionConstraints = 0;
    std::size_t rejectedConstraints = 0;
};

} // namespace imp
