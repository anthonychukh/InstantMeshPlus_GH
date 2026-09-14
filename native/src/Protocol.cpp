#include "Protocol.h"

#include <nlohmann/json.hpp>

#include <array>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <stdexcept>
#include <type_traits>

namespace fs = std::filesystem;
using json = nlohmann::json;

namespace imp {
namespace {

constexpr std::array<char, 8> RequestMagic = {'I', 'M', 'P', 'R', 'E', 'Q', '1', '\0'};
constexpr std::array<char, 8> ResultMagic = {'I', 'M', 'P', 'R', 'E', 'S', '1', '\0'};
constexpr std::uint32_t SchemaVersion = 1;
constexpr std::uint32_t MaximumReasonableCount = 200000000;

class BinaryReader {
public:
    explicit BinaryReader(const fs::path &path) : stream(path, std::ios::binary) {
        if (!stream)
            throw std::runtime_error("Could not open request payload: " + path.string());
    }

    template <typename T>
    T read() {
        static_assert(std::is_trivially_copyable<T>::value, "Binary values must be trivially copyable");
        T value{};
        stream.read(reinterpret_cast<char *>(&value), sizeof(T));
        if (!stream)
            throw std::runtime_error("Unexpected end of request payload");
        return value;
    }

    void readBytes(char *target, std::size_t count) {
        stream.read(target, static_cast<std::streamsize>(count));
        if (!stream)
            throw std::runtime_error("Unexpected end of request payload");
    }

private:
    std::ifstream stream;
};

class BinaryWriter {
public:
    explicit BinaryWriter(const fs::path &path) : stream(path, std::ios::binary | std::ios::trunc) {
        if (!stream)
            throw std::runtime_error("Could not create result payload: " + path.string());
    }

    template <typename T>
    void write(const T &value) {
        static_assert(std::is_trivially_copyable<T>::value, "Binary values must be trivially copyable");
        stream.write(reinterpret_cast<const char *>(&value), sizeof(T));
        if (!stream)
            throw std::runtime_error("Failed while writing result payload");
    }

    void writeBytes(const char *source, std::size_t count) {
        stream.write(source, static_cast<std::streamsize>(count));
        if (!stream)
            throw std::runtime_error("Failed while writing result payload");
    }

private:
    std::ofstream stream;
};

json readJson(const fs::path &path) {
    std::ifstream stream(path);
    if (!stream)
        throw std::runtime_error("Could not open request manifest: " + path.string());
    json value;
    stream >> value;
    return value;
}

void writeJson(const fs::path &path, const json &value) {
    fs::create_directories(path.parent_path());
    std::ofstream stream(path, std::ios::trunc);
    if (!stream)
        throw std::runtime_error("Could not create result manifest: " + path.string());
    stream << value.dump(2) << '\n';
}

fs::path resolveRelative(const fs::path &manifest, const std::string &path) {
    fs::path candidate(path);
    if (candidate.is_relative())
        candidate = manifest.parent_path() / candidate;
    return fs::absolute(candidate);
}

std::uint32_t readCount(BinaryReader &reader, const char *label) {
    const auto value = reader.read<std::uint32_t>();
    if (value > MaximumReasonableCount)
        throw std::runtime_error(std::string(label) + " count exceeds the protocol limit");
    return value;
}

Vec3 readVec3(BinaryReader &reader) {
    return {reader.read<float>(), reader.read<float>(), reader.read<float>()};
}

void writeVec3(BinaryWriter &writer, const Vec3 &value) {
    writer.write(value[0]);
    writer.write(value[1]);
    writer.write(value[2]);
}

TargetMode parseTargetMode(const std::string &value) {
    if (value == "faces")
        return TargetMode::FaceCount;
    if (value == "vertices")
        return TargetMode::VertexCount;
    if (value == "edgeLength")
        return TargetMode::EdgeLength;
    throw std::runtime_error("Unknown target mode: " + value);
}

Settings parseSettings(const json &value) {
    Settings settings;
    settings.targetMode = parseTargetMode(value.value("targetMode", "faces"));
    settings.targetValue = value.value("targetValue", 1000.0f);
    settings.rosy = value.value("rosy", 4);
    settings.posy = value.value("posy", 4);
    settings.extrinsic = value.value("extrinsic", true);
    settings.alignBoundaries = value.value("alignBoundaries", true);
    settings.creaseAngleDegrees = value.value("creaseAngleDegrees", 90.0f);
    settings.smoothIterations = value.value("smoothIterations", 2);
    settings.pureOutput = value.value("pureOutput", true);
    settings.deterministic = value.value("deterministic", false);
    settings.threadCount = value.value("threadCount", -1);
    settings.projectionTolerance = value.value("projectionTolerance", 0.0f);
    return settings;
}

void validateHeader(BinaryReader &reader) {
    std::array<char, 8> magic{};
    reader.readBytes(magic.data(), magic.size());
    if (magic != RequestMagic)
        throw std::runtime_error("Request payload has an invalid magic header");
    if (reader.read<std::uint32_t>() != SchemaVersion)
        throw std::runtime_error("Unsupported request payload schema");
}

} // namespace

Request readRequest(const std::string &manifestPath) {
    const fs::path manifest = fs::absolute(manifestPath);
    const json root = readJson(manifest);
    if (root.value("schema", 0) != static_cast<int>(SchemaVersion))
        throw std::runtime_error("Unsupported request manifest schema");

    Request request;
    request.manifestPath = manifest.string();
    request.resultManifestPath =
        resolveRelative(manifest, root.value("resultManifest", "result.json")).string();
    request.resultPayloadPath =
        resolveRelative(manifest, root.value("resultPayload", "result.impbin")).string();
    request.settings = parseSettings(root.at("settings"));

    const fs::path payload = resolveRelative(manifest, root.at("payload").get<std::string>());
    BinaryReader reader(payload);
    validateHeader(reader);

    const auto vertexCount = readCount(reader, "Vertex");
    const auto triangleCount = readCount(reader, "Triangle");
    const auto orientationCount = readCount(reader, "Orientation constraint");
    const auto positionCount = readCount(reader, "Position constraint");
    const auto guideCount = readCount(reader, "Guide");
    const auto singularityPathCount = readCount(reader, "Singularity path");

    request.vertices.reserve(vertexCount);
    for (std::uint32_t i = 0; i < vertexCount; ++i)
        request.vertices.push_back(readVec3(reader));

    request.triangles.reserve(triangleCount);
    for (std::uint32_t i = 0; i < triangleCount; ++i) {
        request.triangles.push_back({
            reader.read<std::uint32_t>(),
            reader.read<std::uint32_t>(),
            reader.read<std::uint32_t>()});
    }

    request.orientationConstraints.reserve(orientationCount);
    for (std::uint32_t i = 0; i < orientationCount; ++i) {
        request.orientationConstraints.push_back(
            {readVec3(reader), readVec3(reader), reader.read<float>()});
    }

    request.positionConstraints.reserve(positionCount);
    for (std::uint32_t i = 0; i < positionCount; ++i) {
        request.positionConstraints.push_back(
            {readVec3(reader), readVec3(reader), reader.read<float>()});
    }

    request.guides.reserve(guideCount);
    for (std::uint32_t i = 0; i < guideCount; ++i) {
        CurveGuide guide;
        const auto rawMode = reader.read<std::uint32_t>();
        if (rawMode > static_cast<std::uint32_t>(GuideMode::EdgePath))
            throw std::runtime_error("Guide has an invalid mode");
        guide.mode = static_cast<GuideMode>(rawMode);
        guide.weight = reader.read<float>();
        const auto pointCount = readCount(reader, "Guide point");
        guide.points.reserve(pointCount);
        for (std::uint32_t j = 0; j < pointCount; ++j)
            guide.points.push_back(readVec3(reader));
        request.guides.push_back(std::move(guide));
    }

    request.singularityPaths.reserve(singularityPathCount);
    for (std::uint32_t i = 0; i < singularityPathCount; ++i) {
        SingularityPath path;
        const auto rawField = reader.read<std::uint32_t>();
        if (rawField > static_cast<std::uint32_t>(FieldKind::Position))
            throw std::runtime_error("Singularity path has an invalid field kind");
        path.field = static_cast<FieldKind>(rawField);
        const auto pointCount = readCount(reader, "Singularity path point");
        path.points.reserve(pointCount);
        for (std::uint32_t j = 0; j < pointCount; ++j)
            path.points.push_back(readVec3(reader));
        request.singularityPaths.push_back(std::move(path));
    }

    return request;
}

void writeResult(const Request &request, const Result &result) {
    const fs::path payloadPath(request.resultPayloadPath);
    fs::create_directories(payloadPath.parent_path());
    BinaryWriter writer(payloadPath);
    writer.writeBytes(ResultMagic.data(), ResultMagic.size());
    writer.write(SchemaVersion);
    writer.write(static_cast<std::uint32_t>(result.outputVertices.size()));
    writer.write(result.outputFaceArity);
    const auto outputFaceCount = result.outputFaceArity == 0
        ? 0u
        : static_cast<std::uint32_t>(result.outputIndices.size() / result.outputFaceArity);
    writer.write(outputFaceCount);
    writer.write(static_cast<std::uint32_t>(result.field.size()));
    writer.write(static_cast<std::uint32_t>(result.orientationSingularities.size()));
    writer.write(static_cast<std::uint32_t>(result.positionSingularities.size()));

    for (const auto &vertex : result.outputVertices)
        writeVec3(writer, vertex);
    for (const auto index : result.outputIndices)
        writer.write(index);
    for (const auto &sample : result.field) {
        writeVec3(writer, sample.point);
        writeVec3(writer, sample.normal);
        writeVec3(writer, sample.orientation);
        writeVec3(writer, sample.position);
    }
    for (const auto &singularity : result.orientationSingularities) {
        writer.write(singularity.face);
        writer.write(singularity.index);
        writeVec3(writer, singularity.point);
    }
    for (const auto &singularity : result.positionSingularities) {
        writer.write(singularity.face);
        writer.write(singularity.indexU);
        writer.write(singularity.indexV);
        writeVec3(writer, singularity.point);
    }

    json timings = json::object();
    for (const auto &entry : result.timingsMilliseconds)
        timings[entry.first] = entry.second;

    const json manifest = {
        {"schema", SchemaVersion},
        {"status", "ok"},
        {"payload", fs::path(request.resultPayloadPath).filename().string()},
        {"scale", result.scale},
        {"outputVertexCount", result.outputVertices.size()},
        {"outputFaceCount", outputFaceCount},
        {"outputFaceArity", result.outputFaceArity},
        {"fieldSampleCount", result.field.size()},
        {"orientationSingularityCount", result.orientationSingularities.size()},
        {"positionSingularityCount", result.positionSingularities.size()},
        {"appliedOrientationConstraints", result.appliedOrientationConstraints},
        {"appliedPositionConstraints", result.appliedPositionConstraints},
        {"rejectedConstraints", result.rejectedConstraints},
        {"warnings", result.warnings},
        {"timingsMilliseconds", timings}};
    writeJson(request.resultManifestPath, manifest);
}

void writeFailure(const std::string &manifestPath, const std::exception &error) {
    try {
        const fs::path manifest = fs::absolute(manifestPath);
        const json request = readJson(manifest);
        const fs::path resultManifest =
            resolveRelative(manifest, request.value("resultManifest", "result.json"));
        writeJson(resultManifest, {
            {"schema", SchemaVersion},
            {"status", "error"},
            {"message", error.what()}});
    } catch (...) {
        // The original error is more useful than a secondary reporting failure.
    }
}

} // namespace imp
