// SPDX-License-Identifier: MIT

using System.Threading.Channels;

namespace Mia.Emulator.Runtime;

/// <summary>
/// Event-driven execution owner for one emulated hardware block. Native and
/// threaded-browser builds create one persistent thread which sleeps on an
/// event-driven mailbox while idle; only an explicitly single-threaded browser
/// build executes the same work inline.
/// </summary>
internal sealed class MiaWorker : IDisposable
{
    readonly string _name;
#if BROWSER && !BROWSER_THREADS
    readonly bool _runsInline;
    readonly int _inlineThreadId;
    long _inlineWakeCount;
#endif
#if BROWSER
    // A non-dedicated browser worker is owned by the emulator session's sole
    // managed worker. Its counters therefore have one reader/writer and do
    // not need shared-memory atomics on every inline invocation or MMIO
    // access. WebKit makes those otherwise redundant atomics particularly
    // expensive in the emulation hot path.
    long _inlineCompletedWorkCount;
    long _inlineBoundMmioReadCount;
    long _inlineBoundMmioWriteCount;
#endif
#if !BROWSER || BROWSER_THREADS
    readonly bool _dedicatedThread;
#endif
    int _disposedFlag;

#if !BROWSER || BROWSER_THREADS
    // A thread cannot have two synchronous calls outstanding because Invoke
    // blocks it until completion. Reusing that caller's signal avoids creating
    // and destroying a kernel wait handle for every MMIO transaction.
    [ThreadStatic]
    static MiaWorkerSynchronousWaiter? _synchronousWaiter;

    static MiaWorkerSynchronousWaiter GetSynchronousWaiter() =>
        _synchronousWaiter ??= new MiaWorkerSynchronousWaiter();

    [ThreadStatic]
    static MiaWorkerConcurrentWaiter? _concurrentWaiter;

    static MiaWorkerConcurrentWaiter GetConcurrentWaiter() =>
        _concurrentWaiter ??= new MiaWorkerConcurrentWaiter();

    // Channel<T> supplies the atomic, race-free TryWrite/TryComplete pair
    // that makes disposal safe with no lost or orphaned work items. The
    // actual wake signal is a plain AutoResetEvent rather than
    // WaitToReadAsync: converting its ValueTask to a Task and blocking on it
    // allocates a Task and async continuation on every genuine block-then-
    // wake cycle, which measurably doubled CPU time under sustained MMIO
    // traffic versus this hybrid.
    readonly Channel<IMiaWorkerWorkItem> _mailbox = Channel.CreateUnbounded<IMiaWorkerWorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    readonly AutoResetEvent _workAvailable = new(false);
    // A one-time startup barrier, unlike _workAvailable's hot per-wake-cycle
    // signal, so the TaskCompletionSource allocation cost is irrelevant here.
    readonly TaskCompletionSource _started = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    Thread? _thread;
    int _threadId;
    long _wakeCount;
    long _completedWorkCount;
    long _boundMmioReadCount;
    long _boundMmioWriteCount;
    int _pendingMailboxCount;
#endif

    public MiaWorker(string name, bool dedicatedThread = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
#if BROWSER && !BROWSER_THREADS
        _runsInline = true;
        _inlineThreadId = Environment.CurrentManagedThreadId;
        Interlocked.Exchange(ref _inlineWakeCount, 0);
        Interlocked.Exchange(ref _inlineBoundMmioReadCount, 0);
        Interlocked.Exchange(ref _inlineBoundMmioWriteCount, 0);
#endif
#if !BROWSER || BROWSER_THREADS
        _dedicatedThread = dedicatedThread;
        if (_dedicatedThread)
        {
            EnsureStarted();
        }
#endif
    }

    public string Name => _name;

#if BROWSER && !BROWSER_THREADS
    public bool HasDedicatedThread => !_runsInline;

    public int ThreadId => _inlineThreadId;

    public long WakeCount => Interlocked.Read(ref _inlineWakeCount);

    public long CompletedWorkCount => _inlineCompletedWorkCount;

    public long BoundMmioReadCount => Interlocked.Read(ref _inlineBoundMmioReadCount);

    public long BoundMmioWriteCount => Interlocked.Read(ref _inlineBoundMmioWriteCount);

    public bool IsCurrentThread =>
        _runsInline && Environment.CurrentManagedThreadId == _inlineThreadId;
#else
    public bool HasDedicatedThread => _dedicatedThread;

    public int ThreadId => Volatile.Read(ref _threadId);

    public long WakeCount => Interlocked.Read(ref _wakeCount);

    public long CompletedWorkCount =>
#if BROWSER
        _dedicatedThread
            ? Interlocked.Read(ref _completedWorkCount)
            : _inlineCompletedWorkCount;
#else
        Interlocked.Read(ref _completedWorkCount);
#endif

    public long BoundMmioReadCount =>
#if BROWSER
        _dedicatedThread
            ? Interlocked.Read(ref _boundMmioReadCount)
            : _inlineBoundMmioReadCount;
#else
        Interlocked.Read(ref _boundMmioReadCount);
#endif

    public long BoundMmioWriteCount =>
#if BROWSER
        _dedicatedThread
            ? Interlocked.Read(ref _boundMmioWriteCount)
            : _inlineBoundMmioWriteCount;
#else
        Interlocked.Read(ref _boundMmioWriteCount);
#endif

    public bool IsCurrentThread =>
        !_dedicatedThread ||
        Environment.CurrentManagedThreadId == Volatile.Read(ref _threadId);
#endif

    /// <summary>
    /// Raised when fire-and-forget work fails. Request/reply invocations return
    /// their exception directly to the caller instead.
    /// </summary>
    public event Action<Exception>? Faulted;

    bool RaiseFaultedAndContinue(Exception error)
    {
        Faulted?.Invoke(error);
        return true;
    }

    public void Invoke(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposedFlag) != 0, this);
        if (IsCurrentThread)
        {
            action();
            RecordInlineCompletedWork();
            return;
        }

#if BROWSER && !BROWSER_THREADS
        action();
        _inlineCompletedWorkCount++;
#else
        var invocation = new MiaWorkerActionInvocation(
            this,
            action,
            GetSynchronousWaiter());
        Enqueue(invocation);
        invocation.Wait();
#endif
    }

    public TResult Invoke<TResult>(Func<TResult> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposedFlag) != 0, this);
        if (IsCurrentThread)
        {
            var result = function();
            RecordInlineCompletedWork();
            return result;
        }

#if BROWSER && !BROWSER_THREADS
        var inlineResult = function();
        _inlineCompletedWorkCount++;
        return inlineResult;
#else
        var invocation = new MiaWorkerFuncInvocation<TResult>(
            this,
            function,
            GetSynchronousWaiter());
        Enqueue(invocation);
        return invocation.Wait();
#endif
    }

    public ValueTask InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposedFlag) != 0, this);
        if (IsCurrentThread)
        {
            action();
            RecordInlineCompletedWork();
            return ValueTask.CompletedTask;
        }

#if BROWSER && !BROWSER_THREADS
        action();
        _inlineCompletedWorkCount++;
        return ValueTask.CompletedTask;
#else
        var invocation = new MiaWorkerAsyncInvocation(this, action);
        Enqueue(invocation);
        return new ValueTask(invocation.Completion);
#endif
    }

    public ValueTask<TResult> InvokeAsync<TResult>(Func<TResult> function)
    {
        ArgumentNullException.ThrowIfNull(function);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposedFlag) != 0, this);
        if (IsCurrentThread)
        {
            var result = function();
            RecordInlineCompletedWork();
            return ValueTask.FromResult(result);
        }

#if BROWSER && !BROWSER_THREADS
        var inlineResult = function();
        _inlineCompletedWorkCount++;
        return ValueTask.FromResult(inlineResult);
#else
        var invocation = new MiaWorkerAsyncFuncInvocation<TResult>(this, function);
        Enqueue(invocation);
        return new ValueTask<TResult>(invocation.Completion);
#endif
    }

#if !BROWSER || BROWSER_THREADS
    /// <summary>
    /// Starts work on this owner while the caller executes a parallel core
    /// grant. Waiting uses a reusable kernel event and does not spin or poll.
    /// Exactly one concurrent grant may be outstanding per caller thread.
    /// </summary>
    internal MiaWorkerConcurrentInvocation BeginConcurrent(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposedFlag) != 0, this);
        var waiter = GetConcurrentWaiter();
        if (waiter.InUse)
        {
            throw new InvalidOperationException(
                "A caller cannot start overlapping concurrent worker grants.");
        }
        waiter.InUse = true;
        var invocation = new MiaWorkerConcurrentInvocation(this, action, waiter);
        if (IsCurrentThread)
        {
            invocation.Execute();
        }
        else
        {
            Enqueue(invocation);
        }
        return invocation;
    }
#endif

    /// <summary>
    /// Enqueues one-way work and returns immediately. The worker is signaled by
    /// the channel transition and otherwise remains blocked indefinitely.
    /// </summary>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposedFlag) != 0, this);
        if (!HasDedicatedThread)
        {
            ExecutePostedInline(action);
            return;
        }
#if !BROWSER || BROWSER_THREADS
        Enqueue(new MiaWorkerPostedAction(this, action));
#else
        throw new InvalidOperationException(
            "A single-threaded browser worker cannot own a dedicated thread.");
#endif
    }

    void ExecutePostedInline(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error) when (RaiseFaultedAndContinue(error))
        {
        }
        RecordInlineCompletedWork();
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposedFlag, 1, 0) != 0)
        {
            return;
        }

#if !BROWSER || BROWSER_THREADS
        _mailbox.Writer.TryComplete();
        _workAvailable.Set();
        Thread? thread = Volatile.Read(ref _thread);
        if (thread is not null && thread.ManagedThreadId != Environment.CurrentManagedThreadId)
        {
            thread.Join();
        }
        _workAvailable.Dispose();
#endif
    }

#if !BROWSER || BROWSER_THREADS
    void Enqueue(IMiaWorkerWorkItem work)
    {
        EnsureStarted();
        bool written = _mailbox.Writer.TryWrite(work);
        ObjectDisposedException.ThrowIf(!written, this);
        Interlocked.Increment(ref _pendingMailboxCount);
        _workAvailable.Set();
    }

    void EnsureStarted()
    {
        if (!_dedicatedThread)
        {
            return;
        }
        if (Volatile.Read(ref _thread) is not null)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposedFlag) != 0, this);
        var candidate = new Thread(Run)
        {
            IsBackground = true,
            Name = $"Mia {_name}",
        };
        // Dedicated-thread workers only ever reach here synchronously from
        // the constructor, before their reference can be shared with any
        // other thread, so this can't race a concurrent Dispose.
        if (Interlocked.CompareExchange(ref _thread, candidate, null) is null)
        {
            StartWithoutExecutionContext(candidate);
        }
        _started.Task.GetAwaiter().GetResult();
    }

    static void StartWithoutExecutionContext(Thread thread)
    {
        thread.UnsafeStart();
    }

    void Run()
    {
        Volatile.Write(ref _threadId, Environment.CurrentManagedThreadId);
        _started.TrySetResult();
        try
        {
            ChannelReader<IMiaWorkerWorkItem> reader = _mailbox.Reader;
            while (true)
            {
                _workAvailable.WaitOne();
                Interlocked.Increment(ref _wakeCount);
                while (reader.TryRead(out IMiaWorkerWorkItem? work))
                {
                    Interlocked.Decrement(ref _pendingMailboxCount);
                    work.Execute();
                }
                if (reader.Completion.IsCompleted)
                {
                    return;
                }
            }
        }
        catch (Exception error) when (LogFaultAndContinue(error))
        {
        }
    }

    bool LogFaultAndContinue(Exception error)
    {
        Console.Error.WriteLine(
            $"Mia worker '{_name}' failed after " +
            $"{CompletedWorkCount:n0} work item(s): {error}");
        try
        {
            Faulted?.Invoke(error);
        }
        catch (Exception observerError) when (IgnoreFaultObserverFailure(observerError))
        {
        }
        return true;
    }

    // A fault observer cannot recover this execution owner.
    static bool IgnoreFaultObserverFailure(Exception error) => true;

    internal void RecordCompletedWork() => Interlocked.Increment(ref _completedWorkCount);

    internal void OnFaulted(Exception error) => Faulted?.Invoke(error);

    internal void RecordBoundMmioRead()
    {
#if BROWSER
        if (!_dedicatedThread)
        {
            _inlineBoundMmioReadCount++;
            return;
        }
#endif
        Interlocked.Increment(ref _boundMmioReadCount);
    }

    internal void RecordBoundMmioWrite()
    {
#if BROWSER
        if (!_dedicatedThread)
        {
            _inlineBoundMmioWriteCount++;
            return;
        }
#endif
        Interlocked.Increment(ref _boundMmioWriteCount);
    }
#endif

    void RecordInlineCompletedWork()
    {
        if (HasDedicatedThread)
        {
            return;
        }
#if BROWSER && !BROWSER_THREADS
        _inlineCompletedWorkCount++;
#elif BROWSER
        _inlineCompletedWorkCount++;
#else
        RecordCompletedWork();
#endif
    }
}
