// Adapted from specklesystems/GrasshopperAsyncComponent v2.0.3.
// Copyright (c) AEC Systems Ltd. Licensed under Apache-2.0.
// Modified for .NET 8, safe task scheduling, and stale-worker isolation by
// InstantMeshPlus contributors.

using System.Collections.Concurrent;
using System.Diagnostics;
using Grasshopper.Kernel;
using Timer = System.Timers.Timer;

namespace GrasshopperAsyncComponent;

internal sealed class Worker<T> : IDisposable
    where T : GH_Component
{
    public required WorkerInstance<T> Instance { get; init; }

    public required CancellationTokenSource CancellationSource { get; init; }

    public required Func<Task> Work { get; init; }

    public Task? Task { get; private set; }

    public void Start(TaskCreationOptions options) =>
        Task = System.Threading.Tasks.Task.Factory.StartNew(
            Work,
            CancellationSource.Token,
            options,
            TaskScheduler.Default).Unwrap();

    public void Cancel() => CancellationSource.Cancel();

    public void Dispose()
    {
        if (Task?.IsCompleted == true)
            Task.Dispose();
        CancellationSource.Dispose();
    }
}

/// <summary>
/// Speckle's eager asynchronous Grasshopper component pattern, adapted for
/// Rhino 8's .NET 8 runtime.
/// </summary>
public abstract class GH_AsyncComponent<T> : GH_Component, IDisposable
    where T : GH_Component
{
    private readonly Timer _displayProgressTimer;
    private readonly Stopwatch _computationClock = new();
    private readonly object _stateGate = new();
    private readonly List<Worker<T>> _workers = [];
    private TimeSpan _lastComputationTime;
    private int _state;
    private int _setData;
    private int _generation;
    private bool _isDisposed;

    protected GH_AsyncComponent(
        string name,
        string nickname,
        string description,
        string category,
        string subCategory)
        : base(name, nickname, description, category, subCategory)
    {
        ProgressReports = new ConcurrentDictionary<string, double>();
        _displayProgressTimer = new Timer(333) { AutoReset = false };
        _displayProgressTimer.Elapsed += DisplayProgress;
    }

    public ConcurrentDictionary<string, double> ProgressReports { get; }

    public IEnumerable<CancellationTokenSource> CancellationTokenSources
    {
        get
        {
            lock (_stateGate)
                return _workers.Select(worker => worker.CancellationSource).ToArray();
        }
    }

    public IEnumerable<WorkerInstance<T>> Workers
    {
        get
        {
            lock (_stateGate)
                return _workers.Select(worker => worker.Instance).ToArray();
        }
    }

    public int WorkerCount
    {
        get
        {
            lock (_stateGate)
                return _workers.Count;
        }
    }

    public WorkerInstance<T>? BaseWorker { get; set; }

    public TaskCreationOptions TaskCreationOptions { get; set; } = TaskCreationOptions.None;

    public override TimeSpan ProcessorTime
    {
        get
        {
            lock (_stateGate)
            {
                return _computationClock.IsRunning
                    ? _computationClock.Elapsed
                    : _lastComputationTime;
            }
        }
    }

    protected virtual string FormatProgress(double progress) => progress.ToString("0%");

    protected virtual void OnWorkersCompleted() => Message = "Done";

    public virtual void DisplayProgress(object? sender, System.Timers.ElapsedEventArgs e)
    {
        int workerCount;
        double[] progressValues;
        lock (_stateGate)
        {
            workerCount = _workers.Count;
            progressValues = ProgressReports.Values.ToArray();
        }

        if (workerCount == 0 || progressValues.Length == 0)
            return;

        var progress = workerCount == 1
            ? progressValues[^1]
            : progressValues.Sum() / workerCount;
        var message = FormatProgress(progress);
        Rhino.RhinoApp.InvokeOnUiThread((Action)(() =>
        {
            Message = message;
            OnDisplayExpired(true);
        }));
    }

    protected override void BeforeSolveInstance()
    {
        lock (_stateGate)
        {
            if (_state != 0 && _setData == 1)
                return;

            Debug.WriteLine("Cancelling stale async component workers.");
            foreach (var worker in _workers)
            {
                worker.Cancel();
                worker.Dispose();
            }

            ResetStateLocked();
            _computationClock.Restart();
        }
    }

    protected override void AfterSolveInstance()
    {
        Worker<T>[] workers;
        lock (_stateGate)
        {
            if (_state != 0 || _workers.Count == 0 || _setData != 0)
                return;

            workers = _workers.ToArray();
        }

        foreach (var worker in workers)
            worker.Start(TaskCreationOptions);
    }

    protected override void ExpireDownStreamObjects()
    {
        lock (_stateGate)
        {
            if (_setData == 1)
                base.ExpireDownStreamObjects();
        }
    }

    protected override void SolveInstance(IGH_DataAccess access)
    {
        WorkerInstance<T>? workerToPublish = null;
        var finalWorker = false;
        lock (_stateGate)
        {
            if (_state == 0)
            {
                if (BaseWorker is null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Worker class not provided.");
                    CompleteComputationClockLocked();
                    return;
                }

                var generation = _generation;
                var cancellationSource = new CancellationTokenSource();
                var currentWorker = BaseWorker.Duplicate(
                    $"Worker-{access.Iteration}",
                    cancellationSource.Token);
                currentWorker.GetData(access, Params);
                _workers.Add(new Worker<T>
                {
                    Instance = currentWorker,
                    CancellationSource = cancellationSource,
                    Work = () => currentWorker.DoWork(
                        (id, value) => ReportProgress(generation, id, value),
                        () => Done(generation))
                });
                return;
            }

            if (_setData == 0)
                return;

            if (_workers.Count > 0)
            {
                _state--;
                workerToPublish = _workers[_state].Instance;
            }

            finalWorker = _state == 0;
        }

        workerToPublish?.SetData(access);
        if (!finalWorker)
            return;

        lock (_stateGate)
        {
            foreach (var worker in _workers)
                worker.Dispose();

            ResetStateLocked();
            CompleteComputationClockLocked();
        }

        OnWorkersCompleted();
        OnDisplayExpired(true);
    }

    public void RequestCancellation()
    {
        lock (_stateGate)
        {
            foreach (var worker in _workers)
            {
                worker.Cancel();
                worker.Dispose();
            }

            CompleteComputationClockLocked();
            ResetStateLocked();
        }

        Message = "Cancelled";
        OnDisplayExpired(true);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        if (!disposing)
            return;

        lock (_stateGate)
        {
            foreach (var worker in _workers)
            {
                worker.Cancel();
                worker.Dispose();
            }

            CompleteComputationClockLocked();
            ResetStateLocked();
        }

        _displayProgressTimer.Dispose();
    }

    private void Done(int generation)
    {
        var shouldExpire = false;
        lock (_stateGate)
        {
            if (generation != _generation)
                return;

            _state++;
            if (_state == _workers.Count && _setData == 0)
            {
                _setData = 1;
                _workers.Reverse();
                shouldExpire = true;
            }
        }

        if (shouldExpire)
            Rhino.RhinoApp.InvokeOnUiThread((Action)(() => ExpireSolution(true)));
    }

    private void ReportProgress(int generation, string id, double value)
    {
        lock (_stateGate)
        {
            if (generation != _generation)
                return;

            ProgressReports[id] = Math.Clamp(value, 0, 1);
            if (!_displayProgressTimer.Enabled)
                _displayProgressTimer.Start();
        }
    }

    private void ResetStateLocked()
    {
        _generation++;
        _workers.Clear();
        ProgressReports.Clear();
        _state = 0;
        _setData = 0;
    }

    private void CompleteComputationClockLocked()
    {
        if (!_computationClock.IsRunning)
            return;

        _computationClock.Stop();
        _lastComputationTime = _computationClock.Elapsed;
    }
}
