#pragma once

#include "Types.h"

#include <exception>
#include <string>

namespace imp {

Request readRequest(const std::string &manifestPath);
void writeResult(const Request &request, const Result &result);
void writeFailure(const std::string &manifestPath, const std::exception &error);

} // namespace imp
