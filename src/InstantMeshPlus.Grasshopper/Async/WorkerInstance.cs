// Adapted from specklesystems/GrasshopperAsyncComponent v2.0.3.
// Copyright (c) AEC Systems Ltd. Licensed under Apache-2.0.
// Modified for .NET 8 by InstantMeshPlus contributors.

using Grasshopper.Kernel;

namespace GrasshopperAsyncComponent;

/// <summary>
/// Captures one Grasshopper data-matching iteration and performs its heavy
/// work away from the Rhino UI thread.
/// </summary>
public abstract class WorkerInstance<T>(
    T parent,
    string id,
    CancellationToken cancellationToken)
    where T : GH_Component
{
    public T Parent { get; } = parent;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public string Id { get; } = id;

    public abstract WorkerInstance<T> Duplicate(string id, CancellationToken cancellationToken);

    public abstract Task DoWork(Action<string, double> reportProgress, Action done);

    public abstract void SetData(IGH_DataAccess access);

    public abstract void GetData(IGH_DataAccess access, GH_ComponentParamServer parameters);
}
