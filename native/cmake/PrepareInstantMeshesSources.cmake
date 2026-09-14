# Prepare the two upstream Instant Meshes translation units that use TBB APIs
# removed by oneTBB. The pinned submodule stays byte-for-byte unchanged; every
# configure creates deterministic patched copies in the build tree and fails if
# the expected upstream source no longer matches.

function(_imp_replace_exact content_var old_text new_text description)
  string(FIND "${${content_var}}" "${old_text}" match_index)
  if(match_index EQUAL -1)
    message(FATAL_ERROR
      "Could not apply Instant Meshes oneTBB compatibility change: ${description}. "
      "The pinned upstream source has changed and the compatibility overlay must be reviewed.")
  endif()
  string(REPLACE "${old_text}" "${new_text}" updated_content "${${content_var}}")
  set(${content_var} "${updated_content}" PARENT_SCOPE)
endfunction()

function(imp_prepare_instant_meshes_sources source_root output_directory)
  file(MAKE_DIRECTORY "${output_directory}")

  file(READ "${source_root}/bvh.cpp" bvh_source)
  _imp_replace_exact(
    bvh_source
    "struct BVHBuildTask : public tbb::task {"
    "struct BVHBuildTask {"
    "replace the removed tbb::task base class")
  _imp_replace_exact(
    bvh_source
    "    task *execute() {"
    "    void execute() {"
    "make the BVH build operation an ordinary recursive function")
  _imp_replace_exact(
    bvh_source
    "            return nullptr;"
    "            return;"
    "replace legacy task returns")
  _imp_replace_exact(
    bvh_source
    [=[        /* Create an empty parent task */
        tbb::task& c = *new (allocate_continuation()) tbb::empty_task;
        c.set_ref_count(2);

        /* Post right subtree to scheduler */
        BVHBuildTask &b = *new (c.allocate_child())
            BVHBuildTask(bvh, node_idx_right, start + left_count,
                         end, temp + left_count);
        spawn(b);

        /* Directly start working on left subtree */
        recycle_as_child_of(c);
        node_idx = node_idx_left;
        end = start + left_count;

        return this;]=]
    [=[        tbb::parallel_invoke(
            [&] {
                BVHBuildTask(
                    bvh, node_idx_left, start, start + left_count, temp).execute();
            },
            [&] {
                BVHBuildTask(
                    bvh, node_idx_right, start + left_count,
                    end, temp + left_count).execute();
            });]=]
    "replace continuation tasks with oneTBB parallel_invoke")
  _imp_replace_exact(
    bvh_source
    [=[    BVHBuildTask& task = *new(tbb::task::allocate_root())
        BVHBuildTask(*this, 0u, mIndices, mIndices + total_size, temp);
    tbb::task::spawn_root_and_wait(task);]=]
    [=[    BVHBuildTask(
        *this, 0u, mIndices, mIndices + total_size, temp).execute();]=]
    "replace the removed root task API")
  file(WRITE "${output_directory}/bvh.cpp" "${bvh_source}")

  file(READ "${source_root}/field.cpp" field_source)
  _imp_replace_exact(
    field_source
    [=[extern int nprocs;

void Optimizer::run() {]=]
    [=[void Optimizer::run() {]=]
    "remove the legacy global scheduler setting")
  _imp_replace_exact(
    field_source
    "    tbb::task_scheduler_init init(nprocs);\n"
    ""
    "remove the obsolete task_scheduler_init instance")
  file(WRITE "${output_directory}/field.cpp" "${field_source}")

  set(IMP_PATCHED_BVH_SOURCE "${output_directory}/bvh.cpp" PARENT_SCOPE)
  set(IMP_PATCHED_FIELD_SOURCE "${output_directory}/field.cpp" PARENT_SCOPE)
endfunction()
