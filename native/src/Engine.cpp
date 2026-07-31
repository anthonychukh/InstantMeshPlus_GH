#include "Engine.h"

#include "adjacency.h"
#include "bvh.h"
#include "dedge.h"
#include "extract.h"
#include "field.h"
#include "hierarchy.h"
#include "meshstats.h"
#include "normal.h"
#include "subdivide.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <deque>
#include <iostream>
#include <limits>
#include <memory>
#include <queue>
#include <set>
#include <stdexcept>
#include <unordered_map>

extern int nprocs;

extern bool move_orientation_singularity(
    MultiResolutionHierarchy &mRes, std::uint32_t f_src, std::uint32_t f_target);
extern bool move_position_singularity(
    MultiResolutionHierarchy &mRes, std::uint32_t f_src, std::uint32_t f_target);

namespace imp {
namespace {

using Clock = std::chrono::steady_clock;

struct Projection {
    bool valid = false;
    std::uint32_t face = 0;
    Vector3f point = Vector3f::Zero();
    Vector3f barycentric = Vector3f::Zero();
    float distance = std::numeric_limits<float>::infinity();
};

Vector3f eigen(const Vec3 &value) {
    return Vector3f(value[0], value[1], value[2]);
}

Vec3 plain(const Vector3f &value) {
    return {value.x(), value.y(), value.z()};
}

bool finite(const Vector3f &value) {
    return std::isfinite(value.x()) &&
        std::isfinite(value.y()) &&
        std::isfinite(value.z());
}

double elapsedMilliseconds(const Clock::time_point start) {
    return std::chrono::duration<double, std::milli>(Clock::now() - start).count();
}

void progress(const std::string &phase, float value) {
    std::cout << "{\"phase\":\"" << phase << "\",\"progress\":" << value << "}" << std::endl;
}

void validateSettings(const Settings &settings) {
    if (!std::isfinite(settings.targetValue) || settings.targetValue <= 0)
        throw std::runtime_error("Target value must be finite and greater than zero");
    if (settings.rosy != 2 && settings.rosy != 4 && settings.rosy != 6)
        throw std::runtime_error("RoSy must be 2, 4, or 6");
    if (settings.posy != 3 && settings.posy != 4)
        throw std::runtime_error("PoSy must be 4 or 6 (encoded as 4 or 3)");
    if (!std::isfinite(settings.creaseAngleDegrees) ||
        settings.creaseAngleDegrees < -1.0f ||
        settings.creaseAngleDegrees > 180.0f)
        throw std::runtime_error("Crease angle must be disabled with -1 or lie in [0, 180]");
    if (settings.smoothIterations < 0 || settings.smoothIterations > 100)
        throw std::runtime_error("Smoothing iterations must lie in [0, 100]");
    if (settings.threadCount == 0 || settings.threadCount < -1)
        throw std::runtime_error("Thread count must be -1 or greater than zero");
    if (!std::isfinite(settings.projectionTolerance) || settings.projectionTolerance < 0)
        throw std::runtime_error("Projection tolerance must be finite and non-negative");
}

void validateInput(const Request &request) {
    validateSettings(request.settings);
    if (request.vertices.size() < 3)
        throw std::runtime_error("Input mesh must contain at least three vertices");
    if (request.triangles.empty())
        throw std::runtime_error("Input mesh must contain at least one triangle");

    for (const auto &vertex : request.vertices) {
        if (!finite(eigen(vertex)))
            throw std::runtime_error("Input mesh contains a non-finite vertex");
    }
    for (const auto &triangle : request.triangles) {
        if (triangle[0] >= request.vertices.size() ||
            triangle[1] >= request.vertices.size() ||
            triangle[2] >= request.vertices.size())
            throw std::runtime_error("Input mesh contains an out-of-range triangle index");
        if (triangle[0] == triangle[1] ||
            triangle[1] == triangle[2] ||
            triangle[2] == triangle[0])
            throw std::runtime_error("Input mesh contains a degenerate triangle index set");
    }
}

void copyInput(const Request &request, MatrixXu &F, MatrixXf &V) {
    V.resize(3, request.vertices.size());
    for (std::size_t i = 0; i < request.vertices.size(); ++i)
        V.col(static_cast<int>(i)) = eigen(request.vertices[i]);

    F.resize(3, request.triangles.size());
    for (std::size_t i = 0; i < request.triangles.size(); ++i) {
        F(0, static_cast<int>(i)) = request.triangles[i][0];
        F(1, static_cast<int>(i)) = request.triangles[i][1];
        F(2, static_cast<int>(i)) = request.triangles[i][2];
    }
}

float targetScale(const Settings &settings, const MeshStats &stats) {
    if (stats.mSurfaceArea <= 0)
        throw std::runtime_error("Input mesh has zero surface area");

    float faceCount = -1.0f;
    if (settings.targetMode == TargetMode::EdgeLength)
        return settings.targetValue;
    if (settings.targetMode == TargetMode::FaceCount)
        faceCount = settings.targetValue;
    else
        faceCount = settings.posy == 4
            ? settings.targetValue
            : settings.targetValue * 2.0f;

    const float faceArea = static_cast<float>(stats.mSurfaceArea / faceCount);
    if (settings.posy == 4)
        return std::sqrt(faceArea);
    return 2.0f * std::sqrt(faceArea * std::sqrt(1.0f / 3.0f));
}

Projection closestPointOnTriangle(
    const Vector3f &p,
    const Vector3f &a,
    const Vector3f &b,
    const Vector3f &c,
    std::uint32_t face) {
    Projection result;
    result.face = face;

    const Vector3f ab = b - a;
    const Vector3f ac = c - a;
    const Vector3f ap = p - a;
    const float d1 = ab.dot(ap);
    const float d2 = ac.dot(ap);
    if (d1 <= 0.0f && d2 <= 0.0f) {
        result.point = a;
        result.barycentric = Vector3f(1, 0, 0);
    } else {
        const Vector3f bp = p - b;
        const float d3 = ab.dot(bp);
        const float d4 = ac.dot(bp);
        if (d3 >= 0.0f && d4 <= d3) {
            result.point = b;
            result.barycentric = Vector3f(0, 1, 0);
        } else {
            const float vc = d1 * d4 - d3 * d2;
            if (vc <= 0.0f && d1 >= 0.0f && d3 <= 0.0f) {
                const float v = d1 / (d1 - d3);
                result.point = a + v * ab;
                result.barycentric = Vector3f(1.0f - v, v, 0);
            } else {
                const Vector3f cp = p - c;
                const float d5 = ab.dot(cp);
                const float d6 = ac.dot(cp);
                if (d6 >= 0.0f && d5 <= d6) {
                    result.point = c;
                    result.barycentric = Vector3f(0, 0, 1);
                } else {
                    const float vb = d5 * d2 - d1 * d6;
                    if (vb <= 0.0f && d2 >= 0.0f && d6 <= 0.0f) {
                        const float w = d2 / (d2 - d6);
                        result.point = a + w * ac;
                        result.barycentric = Vector3f(1.0f - w, 0, w);
                    } else {
                        const float va = d3 * d6 - d5 * d4;
                        if (va <= 0.0f && (d4 - d3) >= 0.0f && (d5 - d6) >= 0.0f) {
                            const float w = (d4 - d3) /
                                ((d4 - d3) + (d5 - d6));
                            result.point = b + w * (c - b);
                            result.barycentric = Vector3f(0, 1.0f - w, w);
                        } else {
                            const float denom = 1.0f / (va + vb + vc);
                            const float v = vb * denom;
                            const float w = vc * denom;
                            result.point = a + ab * v + ac * w;
                            result.barycentric = Vector3f(1.0f - v - w, v, w);
                        }
                    }
                }
            }
        }
    }

    result.distance = (result.point - p).norm();
    result.valid = true;
    return result;
}

Projection projectPoint(
    const Vector3f &point,
    const MatrixXu &F,
    const MatrixXf &V,
    const BVH &bvh,
    float searchRadius,
    float tolerance) {
    std::vector<std::pair<Float, std::uint32_t>> candidates;
    Float radius = searchRadius;
    bvh.findKNearest(point, 32, radius, candidates, true);

    Projection best;
    for (const auto &candidateEntry : candidates) {
        const std::uint32_t face = candidateEntry.second;
        const Projection candidate = closestPointOnTriangle(
            point,
            V.col(F(0, face)),
            V.col(F(1, face)),
            V.col(F(2, face)),
            face);
        if (!best.valid || candidate.distance < best.distance)
            best = candidate;
    }

    if (best.valid && best.distance <= tolerance)
        return best;
    return Projection{};
}

std::pair<Vector3f, Vector3f> compatibleDirection(
    const Vector3f &a, const Vector3f &normalA,
    const Vector3f &b, const Vector3f &normalB,
    int rosy) {
    if (rosy == 2)
        return compat_orientation_extrinsic_2(a, normalA, b, normalB);
    if (rosy == 4)
        return compat_orientation_extrinsic_4(a, normalA, b, normalB);
    return compat_orientation_extrinsic_6(a, normalA, b, normalB);
}

bool addOrientationAtVertex(
    MultiResolutionHierarchy &hierarchy,
    std::uint32_t vertex,
    Vector3f direction,
    float weight,
    int rosy,
    std::vector<float> &rawWeights) {
    if (weight <= 0)
        return false;
    const Vector3f normal = hierarchy.N().col(vertex);
    direction -= normal * normal.dot(direction);
    const float length = direction.norm();
    if (!finite(direction) || length <= 1e-8f)
        return false;
    direction /= length;

    const float previousWeight = rawWeights[vertex];
    if (previousWeight <= 0) {
        hierarchy.CQ().col(vertex) = direction;
    } else {
        const auto compatible = compatibleDirection(
            hierarchy.CQ().col(vertex), normal, direction, normal, rosy);
        Vector3f blended =
            compatible.first * previousWeight + compatible.second * weight;
        blended -= normal * normal.dot(blended);
        if (blended.squaredNorm() <= 1e-12f)
            return false;
        hierarchy.CQ().col(vertex) = blended.normalized();
    }
    rawWeights[vertex] += weight;
    hierarchy.CQw()[vertex] = std::min(1.0f, rawWeights[vertex]);
    return true;
}

bool addPositionAtVertex(
    MultiResolutionHierarchy &hierarchy,
    std::uint32_t vertex,
    const Vector3f &anchor,
    float weight,
    std::vector<float> &rawWeights) {
    if (weight <= 0 || !finite(anchor))
        return false;
    const float previousWeight = rawWeights[vertex];
    if (previousWeight <= 0)
        hierarchy.CO().col(vertex) = anchor;
    else
        hierarchy.CO().col(vertex) =
            (hierarchy.CO().col(vertex) * previousWeight + anchor * weight) /
            (previousWeight + weight);
    rawWeights[vertex] += weight;
    hierarchy.COw()[vertex] = std::min(1.0f, rawWeights[vertex]);
    return true;
}

bool applyOrientationProjection(
    MultiResolutionHierarchy &hierarchy,
    const Projection &projection,
    const Vector3f &direction,
    float weight,
    int rosy,
    std::vector<float> &rawWeights) {
    bool applied = false;
    for (int corner = 0; corner < 3; ++corner) {
        const auto vertex = hierarchy.F()(corner, projection.face);
        const float influence = weight * std::max(0.0f, projection.barycentric[corner]);
        applied |= addOrientationAtVertex(
            hierarchy, vertex, direction, influence, rosy, rawWeights);
    }
    return applied;
}

bool applyPositionProjection(
    MultiResolutionHierarchy &hierarchy,
    const Projection &projection,
    const Vector3f &anchor,
    float weight,
    std::vector<float> &rawWeights) {
    bool applied = false;
    for (int corner = 0; corner < 3; ++corner) {
        const auto vertex = hierarchy.F()(corner, projection.face);
        const float influence = weight * std::max(0.0f, projection.barycentric[corner]);
        applied |= addPositionAtVertex(
            hierarchy, vertex, anchor, influence, rawWeights);
    }
    return applied;
}

Vector3f curveTangent(const std::vector<Vec3> &points, std::size_t index) {
    if (index == 0)
        return eigen(points[1]) - eigen(points[0]);
    if (index + 1 == points.size())
        return eigen(points[index]) - eigen(points[index - 1]);
    return eigen(points[index + 1]) - eigen(points[index - 1]);
}

std::vector<std::uint32_t> shortestFacePath(
    std::uint32_t source,
    std::uint32_t target,
    const VectorXu &E2E,
    std::size_t faceCount) {
    if (source == target)
        return {source};

    std::vector<std::int64_t> previous(faceCount, -1);
    std::queue<std::uint32_t> queue;
    previous[source] = source;
    queue.push(source);

    while (!queue.empty() && previous[target] < 0) {
        const auto face = queue.front();
        queue.pop();
        for (int edge = 0; edge < 3; ++edge) {
            const auto opposite = E2E[3 * face + edge];
            if (opposite == INVALID)
                continue;
            const auto neighbor = opposite / 3;
            if (neighbor >= faceCount || previous[neighbor] >= 0)
                continue;
            previous[neighbor] = face;
            queue.push(neighbor);
        }
    }

    if (previous[target] < 0)
        return {};

    std::vector<std::uint32_t> path;
    for (auto current = target;; current = static_cast<std::uint32_t>(previous[current])) {
        path.push_back(current);
        if (current == source)
            break;
    }
    std::reverse(path.begin(), path.end());
    return path;
}

std::vector<std::uint32_t> buildProjectedPath(
    const SingularityPath &input,
    std::uint32_t sourceFace,
    const MatrixXu &F,
    const MatrixXf &V,
    const BVH &bvh,
    const VectorXu &E2E,
    float searchRadius,
    float tolerance) {
    std::vector<std::uint32_t> controlFaces;
    controlFaces.push_back(sourceFace);
    for (const auto &point : input.points) {
        const auto projection = projectPoint(
            eigen(point), F, V, bvh, searchRadius, tolerance);
        if (!projection.valid)
            continue;
        if (controlFaces.back() != projection.face)
            controlFaces.push_back(projection.face);
    }

    std::vector<std::uint32_t> path;
    for (std::size_t i = 1; i < controlFaces.size(); ++i) {
        auto segment = shortestFacePath(
            controlFaces[i - 1], controlFaces[i], E2E, F.cols());
        if (segment.empty())
            return {};
        if (!path.empty())
            segment.erase(segment.begin());
        path.insert(path.end(), segment.begin(), segment.end());
    }
    if (path.empty())
        path.push_back(sourceFace);
    return path;
}

template <typename SingularityMap>
std::uint32_t closestSingularityFace(
    const SingularityMap &singularities,
    const Vector3f &point,
    const MultiResolutionHierarchy &hierarchy,
    float tolerance) {
    std::uint32_t bestFace = INVALID;
    float bestDistance = tolerance;
    for (const auto &entry : singularities) {
        const std::uint32_t face = entry.first;
        const float distance = (hierarchy.faceCenter(face) - point).norm();
        if (distance <= bestDistance) {
            bestDistance = distance;
            bestFace = face;
        }
    }
    return bestFace;
}

class OptimizerSession {
public:
    explicit OptimizerSession(MultiResolutionHierarchy &hierarchy)
        : optimizer(hierarchy, false) {}

    ~OptimizerSession() {
        if (!closed)
            optimizer.shutdown();
    }

    void close() {
        if (!closed) {
            optimizer.shutdown();
            closed = true;
        }
    }

    Optimizer optimizer;

private:
    bool closed = false;
};

void solveOrientation(Optimizer &optimizer) {
    optimizer.optimizeOrientations(-1);
    optimizer.notify();
    optimizer.wait();
}

void solvePosition(Optimizer &optimizer) {
    optimizer.optimizePositions(-1);
    optimizer.notify();
    optimizer.wait();
}

bool movePath(
    Optimizer &optimizer,
    const std::vector<std::uint32_t> &path,
    bool orientation) {
    if (path.size() < 2)
        return false;
    std::vector<std::uint32_t> reversed(path.rbegin(), path.rend());
    optimizer.moveSingularity(reversed, orientation);
    if (orientation)
        optimizer.optimizeOrientations(0);
    else
        optimizer.optimizePositions(0);
    optimizer.notify();
    optimizer.wait();
    return true;
}

} // namespace

Result solve(const Request &request) {
    validateInput(request);
    nprocs = request.settings.threadCount;
    Result result;
    const auto totalStart = Clock::now();

    MatrixXu F;
    MatrixXf V;
    copyInput(request, F, V);

    progress("preprocess", 0.0f);
    auto phaseStart = Clock::now();
    MeshStats stats = compute_mesh_stats(F, V, request.settings.deterministic);
    const float scale = targetScale(request.settings, stats);
    if (!std::isfinite(scale) || scale <= 0)
        throw std::runtime_error("Derived target edge length is invalid");
    result.scale = scale;

    VectorXu V2E;
    VectorXu E2E;
    VectorXb boundary;
    VectorXb nonManifold;
    if (stats.mMaximumEdgeLength * 2 > scale ||
        stats.mMaximumEdgeLength > stats.mAverageEdgeLength * 2) {
        build_dedge(F, V, V2E, E2E, boundary, nonManifold);
        subdivide(
            F, V, V2E, E2E, boundary, nonManifold,
            std::min(scale / 2.0f, static_cast<float>(stats.mAverageEdgeLength * 2)),
            request.settings.deterministic);
        stats = compute_mesh_stats(F, V, request.settings.deterministic);
    }

    build_dedge(F, V, V2E, E2E, boundary, nonManifold);
    auto adjacency = generate_adjacency_matrix_uniform(F, V2E, E2E, nonManifold);
    MatrixXf N;
    std::set<std::uint32_t> creaseInput;
    if (request.settings.creaseAngleDegrees >= 0)
        generate_crease_normals(
            F, V, V2E, E2E, boundary, nonManifold,
            request.settings.creaseAngleDegrees, N, creaseInput);
    else
        generate_smooth_normals(F, V, V2E, E2E, nonManifold, N);

    VectorXf A;
    compute_dual_vertex_areas(F, V, V2E, E2E, nonManifold, A);

    MultiResolutionHierarchy hierarchy;
    hierarchy.setAdj(std::move(adjacency));
    hierarchy.setF(std::move(F));
    hierarchy.setV(std::move(V));
    hierarchy.setA(std::move(A));
    hierarchy.setN(std::move(N));
    hierarchy.setE2E(std::move(E2E));
    hierarchy.setScale(scale);
    hierarchy.build(request.settings.deterministic);
    hierarchy.resetSolution();
    hierarchy.clearConstraints();

    const float tolerance = request.settings.projectionTolerance > 0
        ? request.settings.projectionTolerance
        : std::max(scale * 0.1f, 1e-5f);
    const float searchRadius = std::max(
        tolerance * 2.0f,
        (stats.mAABB.max - stats.mAABB.min).norm() * 2.0f + tolerance);

    BVH bvh(&hierarchy.F(), &hierarchy.V(), &hierarchy.N(), stats.mAABB);
    bvh.build();

    std::vector<float> rawOrientationWeights(hierarchy.size(), 0.0f);
    std::vector<float> rawPositionWeights(hierarchy.size(), 0.0f);

    if (request.settings.alignBoundaries) {
        for (std::uint32_t edgeIndex = 0;
             edgeIndex < static_cast<std::uint32_t>(3 * hierarchy.F().cols());
             ++edgeIndex) {
            if (hierarchy.E2E()[edgeIndex] != INVALID)
                continue;
            const auto face = edgeIndex / 3;
            const auto corner = edgeIndex % 3;
            const auto i0 = hierarchy.F()(corner, face);
            const auto i1 = hierarchy.F()((corner + 1) % 3, face);
            const Vector3f p0 = hierarchy.V().col(i0);
            const Vector3f p1 = hierarchy.V().col(i1);
            const Vector3f edge = p1 - p0;
            if (edge.squaredNorm() <= 1e-12f)
                continue;
            addOrientationAtVertex(
                hierarchy, i0, edge.normalized(), 1.0f,
                request.settings.rosy, rawOrientationWeights);
            addOrientationAtVertex(
                hierarchy, i1, edge.normalized(), 1.0f,
                request.settings.rosy, rawOrientationWeights);
            addPositionAtVertex(hierarchy, i0, p0, 1.0f, rawPositionWeights);
            addPositionAtVertex(hierarchy, i1, p1, 1.0f, rawPositionWeights);
        }
    }

    for (const auto &constraint : request.orientationConstraints) {
        const auto projection = projectPoint(
            eigen(constraint.point), hierarchy.F(), hierarchy.V(), bvh,
            searchRadius, tolerance);
        if (!projection.valid ||
            !applyOrientationProjection(
                hierarchy, projection, eigen(constraint.direction),
                std::max(0.0f, std::min(1.0f, constraint.weight)),
                request.settings.rosy, rawOrientationWeights)) {
            ++result.rejectedConstraints;
        } else {
            ++result.appliedOrientationConstraints;
        }
    }

    for (const auto &constraint : request.positionConstraints) {
        const auto projection = projectPoint(
            eigen(constraint.point), hierarchy.F(), hierarchy.V(), bvh,
            searchRadius, tolerance);
        if (!projection.valid ||
            !applyPositionProjection(
                hierarchy, projection, eigen(constraint.anchor),
                std::max(0.0f, std::min(1.0f, constraint.weight)),
                rawPositionWeights)) {
            ++result.rejectedConstraints;
        } else {
            ++result.appliedPositionConstraints;
        }
    }

    for (const auto &guide : request.guides) {
        if (guide.points.size() < 2) {
            ++result.rejectedConstraints;
            result.warnings.push_back("A guide with fewer than two points was ignored");
            continue;
        }
        const float weight = std::max(0.0f, std::min(1.0f, guide.weight));
        for (std::size_t pointIndex = 0; pointIndex < guide.points.size(); ++pointIndex) {
            const auto projection = projectPoint(
                eigen(guide.points[pointIndex]),
                hierarchy.F(), hierarchy.V(), bvh, searchRadius, tolerance);
            const Vector3f tangent = curveTangent(guide.points, pointIndex);
            const bool orientationApplied =
                projection.valid &&
                applyOrientationProjection(
                    hierarchy, projection, tangent, weight,
                    request.settings.rosy, rawOrientationWeights);
            if (orientationApplied)
                ++result.appliedOrientationConstraints;
            else
                ++result.rejectedConstraints;

            if (guide.mode == GuideMode::EdgePath && projection.valid) {
                if (applyPositionProjection(
                    hierarchy, projection, projection.point, weight,
                    rawPositionWeights))
                    ++result.appliedPositionConstraints;
                else
                    ++result.rejectedConstraints;
            }
        }
    }

    hierarchy.propagateConstraints(request.settings.rosy, request.settings.posy);
    result.timingsMilliseconds["preprocess"] = elapsedMilliseconds(phaseStart);
    progress("preprocess", 1.0f);

    OptimizerSession optimizer(hierarchy);
    optimizer.optimizer.setRoSy(request.settings.rosy);
    optimizer.optimizer.setPoSy(request.settings.posy);
    optimizer.optimizer.setExtrinsic(request.settings.extrinsic);

    phaseStart = Clock::now();
    progress("orientation", 0.0f);
    solveOrientation(optimizer.optimizer);

    std::map<std::uint32_t, std::uint32_t> orientationSingularities;
    compute_orientation_singularities(
        hierarchy, orientationSingularities,
        request.settings.extrinsic, request.settings.rosy);

    for (const auto &path : request.singularityPaths) {
        if (path.field != FieldKind::Orientation)
            continue;
        if (request.settings.rosy != 4 || request.settings.posy != 4) {
            result.warnings.push_back(
                "Orientation singularity movement requires 4-RoSy/4-PoSy");
            ++result.rejectedConstraints;
            continue;
        }
        if (path.points.size() < 2) {
            result.warnings.push_back("An orientation singularity path was too short");
            ++result.rejectedConstraints;
            continue;
        }
        const auto source = closestSingularityFace(
            orientationSingularities, eigen(path.points.front()),
            hierarchy, std::max(tolerance, scale));
        if (source == INVALID) {
            result.warnings.push_back(
                "No orientation singularity was found near a path start");
            ++result.rejectedConstraints;
            continue;
        }
        auto faces = buildProjectedPath(
            path, source, hierarchy.F(), hierarchy.V(), bvh, hierarchy.E2E(),
            searchRadius, tolerance);
        if (!movePath(optimizer.optimizer, faces, true)) {
            result.warnings.push_back("An orientation singularity path could not be moved");
            ++result.rejectedConstraints;
        }
        orientationSingularities.clear();
        compute_orientation_singularities(
            hierarchy, orientationSingularities,
            request.settings.extrinsic, request.settings.rosy);
    }
    result.timingsMilliseconds["orientation"] = elapsedMilliseconds(phaseStart);
    progress("orientation", 1.0f);

    phaseStart = Clock::now();
    progress("position", 0.0f);
    solvePosition(optimizer.optimizer);
    std::map<std::uint32_t, Vector2i> positionSingularities;
    compute_position_singularities(
        hierarchy, orientationSingularities, positionSingularities,
        request.settings.extrinsic, request.settings.rosy, request.settings.posy);

    for (const auto &path : request.singularityPaths) {
        if (path.field != FieldKind::Position)
            continue;
        if (request.settings.rosy != 4 || request.settings.posy != 4) {
            result.warnings.push_back(
                "Position singularity movement requires 4-RoSy/4-PoSy");
            ++result.rejectedConstraints;
            continue;
        }
        if (path.points.size() < 2) {
            result.warnings.push_back("A position singularity path was too short");
            ++result.rejectedConstraints;
            continue;
        }
        const auto source = closestSingularityFace(
            positionSingularities, eigen(path.points.front()),
            hierarchy, std::max(tolerance, scale));
        if (source == INVALID) {
            result.warnings.push_back(
                "No position singularity was found near a path start");
            ++result.rejectedConstraints;
            continue;
        }
        auto faces = buildProjectedPath(
            path, source, hierarchy.F(), hierarchy.V(), bvh, hierarchy.E2E(),
            searchRadius, tolerance);
        if (!movePath(optimizer.optimizer, faces, false)) {
            result.warnings.push_back("A position singularity path could not be moved");
            ++result.rejectedConstraints;
        }
        positionSingularities.clear();
        compute_position_singularities(
            hierarchy, orientationSingularities, positionSingularities,
            request.settings.extrinsic, request.settings.rosy, request.settings.posy);
    }
    result.timingsMilliseconds["position"] = elapsedMilliseconds(phaseStart);
    progress("position", 1.0f);
    optimizer.close();

    phaseStart = Clock::now();
    progress("extract", 0.0f);
    MatrixXf outputVertices;
    MatrixXf outputNormals;
    MatrixXf outputFaceNormals;
    std::vector<std::vector<TaggedLink>> extractedAdjacency;
    std::set<std::uint32_t> creaseOutput;
    extract_graph(
        hierarchy, request.settings.extrinsic,
        request.settings.rosy, request.settings.posy,
        extractedAdjacency, outputVertices, outputNormals,
        creaseInput, creaseOutput, request.settings.deterministic);

    MatrixXu outputFaces;
    BVH *reprojectionBvh =
        request.settings.smoothIterations > 0 ? &bvh : nullptr;
    extract_faces(
        extractedAdjacency, outputVertices, outputNormals, outputFaceNormals,
        outputFaces, request.settings.posy, hierarchy.scale(), creaseOutput,
        true, request.settings.pureOutput, reprojectionBvh,
        request.settings.smoothIterations);
    if (outputFaces.cols() == 0 || outputFaces.rows() < 3 || outputFaces.rows() > 4)
        throw std::runtime_error("Instant Meshes extraction did not produce a valid face set");

    result.outputFaceArity = static_cast<std::uint32_t>(outputFaces.rows());
    result.outputVertices.reserve(outputVertices.cols());
    for (int i = 0; i < outputVertices.cols(); ++i)
        result.outputVertices.push_back(plain(outputVertices.col(i)));
    result.outputIndices.reserve(outputFaces.size());
    for (int face = 0; face < outputFaces.cols(); ++face)
        for (int corner = 0; corner < outputFaces.rows(); ++corner)
            result.outputIndices.push_back(outputFaces(corner, face));

    result.field.reserve(hierarchy.size());
    for (std::uint32_t i = 0; i < hierarchy.size(); ++i) {
        result.field.push_back({
            plain(hierarchy.V().col(i)),
            plain(hierarchy.N().col(i)),
            plain(hierarchy.Q().col(i)),
            plain(hierarchy.O().col(i))});
    }

    for (const auto &entry : orientationSingularities) {
        const std::uint32_t face = entry.first;
        const std::uint32_t index = entry.second;
        result.orientationSingularities.push_back(
            {face, static_cast<std::int32_t>(index), plain(hierarchy.faceCenter(face))});
    }
    for (const auto &entry : positionSingularities) {
        const std::uint32_t face = entry.first;
        const Vector2i &index = entry.second;
        result.positionSingularities.push_back(
            {face, index.x(), index.y(), plain(hierarchy.faceCenter(face))});
    }

    result.timingsMilliseconds["extract"] = elapsedMilliseconds(phaseStart);
    result.timingsMilliseconds["total"] = elapsedMilliseconds(totalStart);
    progress("extract", 1.0f);
    return result;
}

} // namespace imp
