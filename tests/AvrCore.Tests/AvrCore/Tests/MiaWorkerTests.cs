// SPDX-License-Identifier: MIT

using AvrCore;
using Mia.Emulator;
using Xunit;

namespace AvrCore.Tests;

public sealed class MiaWorkerTests
{
    [Fact]
    public void NativeWorkersOwnStableDistinctThreads()
    {
        using var first = new MiaWorker("test first");
        using var second = new MiaWorker("test second");
        var caller = Environment.CurrentManagedThreadId;

        Assert.NotEqual(0, first.ThreadId);
        Assert.NotEqual(0, second.ThreadId);
        Assert.NotEqual(first.ThreadId, second.ThreadId);

        var firstThread = first.Invoke(() => Environment.CurrentManagedThreadId);
        var secondThread = second.Invoke(() => Environment.CurrentManagedThreadId);

        Assert.True(first.HasDedicatedThread);
        Assert.NotEqual(caller, firstThread);
        Assert.NotEqual(caller, secondThread);
        Assert.NotEqual(firstThread, secondThread);
        Assert.Equal(firstThread, first.Invoke(() => Environment.CurrentManagedThreadId));
    }

    [Fact]
    public void DedicatedThreadDoesNotInheritCallerExecutionContext()
    {
        var ambient = new AsyncLocal<string?>();
        ambient.Value = "caller";

        using var worker = new MiaWorker("test execution context");

        Assert.Null(worker.Invoke(() => ambient.Value));
        Assert.Equal("caller", ambient.Value);
    }

    [Fact]
    public async Task AsyncAndPostedWorkWakeWithoutPolling()
    {
        using var worker = new MiaWorker("test async");
        var values = new List<int>();
        var posted = new TaskCompletionSource();

        worker.Post(() =>
        {
            values.Add(1);
            posted.TrySetResult();
        });
        await posted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        await worker.InvokeAsync(() => values.Add(2)).ConfigureAwait(true);

        Assert.Equal([1, 2], values);
        Assert.Equal(2, worker.CompletedWorkCount);
        Assert.InRange(worker.WakeCount, 1, 2);
    }

    [Fact]
    public void ConcurrentGrantBlocksOnAnEventAndReturnsOwnerFailures()
    {
        using var worker = new MiaWorker("test concurrent grant");
        var release = new TaskCompletionSource();
        var ownerThread = worker.ThreadId;
        var executedThread = 0;

        var grant = worker.BeginConcurrent(() =>
        {
            release.Task.GetAwaiter().GetResult();
            executedThread = Environment.CurrentManagedThreadId;
        });
        Assert.False(grant.IsCompleted);
        release.TrySetResult();
        grant.Wait();

        Assert.True(grant.IsCompleted);
        Assert.Equal(ownerThread, executedThread);

        var failed = worker.BeginConcurrent(() =>
            throw new InvalidOperationException("grant failed"));
        var error = Assert.Throws<InvalidOperationException>(failed.Wait);
        Assert.Equal("grant failed", error.Message);
    }

    [Fact]
    public void CallerCannotOverlapConcurrentWorkerGrants()
    {
        using var worker = new MiaWorker("test overlapping grant");
        var release = new TaskCompletionSource();
        var grant = worker.BeginConcurrent(() => release.Task.GetAwaiter().GetResult());

        Assert.Throws<InvalidOperationException>(() =>
            worker.BeginConcurrent(() => { }));

        release.TrySetResult();
        grant.Wait();
    }

    [Fact]
    public void InvocationReturnsExceptionsToTheCaller()
    {
        using var worker = new MiaWorker("test exception");

        var error = Assert.Throws<InvalidOperationException>(
            () => worker.Invoke(() => throw new InvalidOperationException("device failed")));

        Assert.Equal("device failed", error.Message);
    }

    [Fact]
    public void RepeatedSynchronousInvocationsCompleteOnTheOwnerThread()
    {
        const int Iterations = 10_000;
        using var worker = new MiaWorker("test repeated sync");
        var ownerThread = worker.ThreadId;
        var value = 0;

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            worker.Invoke(() =>
            {
                Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
                value++;
            });
            var observed = worker.Invoke(() =>
            {
                Assert.Equal(ownerThread, Environment.CurrentManagedThreadId);
                return value;
            });

            Assert.Equal(iteration + 1, observed);
        }

        Assert.Equal(Iterations, value);
        Assert.Equal(Iterations * 2L, worker.CompletedWorkCount);
        Assert.InRange(worker.WakeCount, 1, Iterations * 2L);
    }

    [Fact]
    public void FailedSynchronousInvocationsDoNotPoisonTheWorker()
    {
        using var worker = new MiaWorker("test exception recovery");
        var ownerThread = worker.ThreadId;

        var actionError = Assert.Throws<InvalidOperationException>(() =>
            worker.Invoke(() =>
                throw new InvalidOperationException("action failed")));
        var functionError = Assert.Throws<ArgumentException>(() =>
            worker.Invoke<int>(() =>
                throw new ArgumentException("function failed")));

        Assert.Equal("action failed", actionError.Message);
        Assert.Equal("function failed", functionError.Message);
        Assert.Equal(2, worker.CompletedWorkCount);

        worker.Invoke(() => Assert.Equal(
            ownerThread,
            Environment.CurrentManagedThreadId));
        var result = worker.Invoke(() =>
            (Thread: Environment.CurrentManagedThreadId, Value: 0x68));

        Assert.Equal(ownerThread, result.Thread);
        Assert.Equal(0x68, result.Value);
        Assert.Equal(4, worker.CompletedWorkCount);
    }

    [Fact]
    public async Task ConcurrentSynchronousCallersAreSerializedByOneOwner()
    {
        const int CallerCount = 12;
        const int InvocationsPerCaller = 500;
        const int TotalInvocations = CallerCount * InvocationsPerCaller;
        using var worker = new MiaWorker("test concurrent sync");
        var ownerThread = worker.ThreadId;
        var sequence = 0;
        var results = new int[TotalInvocations];
        var start = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var callers = Enumerable.Range(0, CallerCount).Select(caller =>
            Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(true);
                for (var iteration = 0;
                     iteration < InvocationsPerCaller;
                     iteration++)
                {
                    var resultIndex =
                        caller * InvocationsPerCaller + iteration;
                    results[resultIndex] = worker.Invoke(() =>
                    {
                        Assert.Equal(
                            ownerThread,
                            Environment.CurrentManagedThreadId);
                        return ++sequence;
                    });
                }
            })).ToArray();

        start.SetResult();
        await Task.WhenAll(callers).ConfigureAwait(true);

        Assert.Equal(TotalInvocations, sequence);
        Assert.Equal(
            Enumerable.Range(1, TotalInvocations),
            results.Order());
        Assert.Equal(TotalInvocations, worker.CompletedWorkCount);
        Assert.InRange(worker.WakeCount, 1, TotalInvocations);
    }

    [Fact]
    public async Task ConcurrentPostedWorkCompletesExactlyOnceWithNoLostItems()
    {
        const int PosterCount = 16;
        const int PostsPerPoster = 2_000;
        const int TotalPosts = PosterCount * PostsPerPoster;
        using var worker = new MiaWorker("test concurrent posted");
        var completedCount = 0;
        var start = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var posters = Enumerable.Range(0, PosterCount).Select(_ =>
            Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(true);
                for (var iteration = 0; iteration < PostsPerPoster; iteration++)
                {
                    worker.Post(() => Interlocked.Increment(ref completedCount));
                }
            })).ToArray();

        start.SetResult();
        await Task.WhenAll(posters).ConfigureAwait(true);

        // Every poster's writes complete-before this call begins, so this
        // final synchronous round trip cannot observe the owner's mailbox as
        // drained until every posted item ahead of it has already run.
        await worker.InvokeAsync(() => { }).ConfigureAwait(true);

        Assert.Equal(TotalPosts, completedCount);
        Assert.Equal(TotalPosts + 1, worker.CompletedWorkCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MmioPostReplyEffectReturnsToAvrOwner(bool dedicatedThread)
    {
        var cpu = new Cpu(new byte[2], 0x100);
        using var worker = new MiaWorker("test MMIO owner", dedicatedThread);
        var callerThread = Environment.CurrentManagedThreadId;
        var readThread = 0;
        var takeThread = 0;
        var postReplyThread = 0;
        cpu.ReadHooks[0x40] = _ =>
        {
            readThread = Environment.CurrentManagedThreadId;
            return 0x5a;
        };
        MiaMmioWorkerBinding.BindReadWithPostReply(
            cpu,
            worker,
            0x40,
            () =>
            {
                takeThread = Environment.CurrentManagedThreadId;
                return true;
            },
            () => postReplyThread = Environment.CurrentManagedThreadId);

        Assert.Equal(0x5a, cpu.ReadData(0x40));

        int ownerThread = dedicatedThread ? worker.ThreadId : callerThread;
        Assert.Equal(ownerThread, readThread);
        Assert.Equal(ownerThread, takeThread);
        Assert.Equal(callerThread, postReplyThread);
    }

    [Fact]
    public void InlineMmioPostReplyDoesNotAllocatePerRead()
    {
        var cpu = new Cpu(new byte[2], 0x100);
        using var worker = new MiaWorker("test MMIO owner", dedicatedThread: false);
        int replies = 0;
        cpu.ReadHooks[0x40] = _ => 0x5a;
        MiaMmioWorkerBinding.BindReadWithPostReply(
            cpu, worker, 0x40, () => true, () => replies++);
        cpu.ReadData(0x40);
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

        for (var index = 0; index < 100_000; index++)
        {
            cpu.ReadData(0x40);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Assert.Equal(100_001, replies);
        Assert.True(allocated < 1_024, $"Inline MMIO reads allocated {allocated:N0} bytes.");
    }

    [Fact]
    public void WriteOnlyBindingLeavesAtomicReadSnapshotOnTheCaller()
    {
        var cpu = new Cpu(new byte[2], 0x100);
        using var worker = new MiaWorker("test write-only MMIO owner");
        var callerThread = Environment.CurrentManagedThreadId;
        var readThread = 0;
        var writeThread = 0;
        cpu.ReadHooks[0x40] = _ =>
        {
            readThread = Environment.CurrentManagedThreadId;
            return 0x68;
        };
        cpu.WriteHooks[0x40] = (_, _, _, _) =>
        {
            writeThread = Environment.CurrentManagedThreadId;
            return false;
        };
        MiaMmioWorkerBinding.BindWrites(cpu, worker, 0x40);

        Assert.Equal(0x68, cpu.ReadData(0x40));
        cpu.WriteData(0x40, 0x5a);

        Assert.Equal(callerThread, readThread);
        Assert.Equal(worker.ThreadId, writeThread);
        Assert.Equal(0, worker.BoundMmioReadCount);
        Assert.Equal(1, worker.BoundMmioWriteCount);
    }
}
