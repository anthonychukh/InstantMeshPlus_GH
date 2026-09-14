#pragma once

#include <algorithm>
#include <cstddef>
#include <iterator>
#include <tbb/parallel_invoke.h>

// oneTBB removed the low-level task API used by the historical PSS helper.
// Keep the same call surface with a stable parallel merge sort implemented on
// the supported parallel_invoke API.
namespace pss {

template <typename RandomAccessIterator, typename Compare>
void parallel_stable_sort(
    RandomAccessIterator first,
    RandomAccessIterator last,
    Compare compare) {
    constexpr std::ptrdiff_t serialCutoff = 2048;
    const auto count = last - first;
    if (count <= serialCutoff) {
        std::stable_sort(first, last, compare);
        return;
    }

    const auto middle = first + count / 2;
    tbb::parallel_invoke(
        [&] { parallel_stable_sort(first, middle, compare); },
        [&] { parallel_stable_sort(middle, last, compare); });
    std::inplace_merge(first, middle, last, compare);
}

} // namespace pss
