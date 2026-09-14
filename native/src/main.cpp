#include "Engine.h"
#include "Protocol.h"

#include <exception>
#include <iostream>
#include <string>

namespace {

std::string requestPath(int argc, char **argv) {
    if (argc == 2 && std::string(argv[1]) == "--help")
        return {};
    if (argc == 3 && std::string(argv[1]) == "--request")
        return argv[2];
    throw std::runtime_error(
        "Syntax: InstantMeshPlus.Worker --request <request.json>");
}

} // namespace

int main(int argc, char **argv) {
    std::string manifest;
    try {
        manifest = requestPath(argc, argv);
        if (manifest.empty()) {
            std::cout << "InstantMeshPlus.Worker --request <request.json>" << std::endl;
            return 0;
        }

        const auto request = imp::readRequest(manifest);
        const auto result = imp::solve(request);
        imp::writeResult(request, result);
        return 0;
    } catch (const std::exception &error) {
        if (!manifest.empty())
            imp::writeFailure(manifest, error);
        std::cerr << error.what() << std::endl;
        return 1;
    }
}
