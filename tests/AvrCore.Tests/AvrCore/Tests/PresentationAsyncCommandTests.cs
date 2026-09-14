// SPDX-License-Identifier: MIT

using Mia.Emulator.Presentation;
using Xunit;

namespace AvrCore.Tests;

public sealed class PresentationAsyncCommandTests
{
    [Fact]
    public async Task ExecuteAsyncPreventsOverlappingExecutionByDefault()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;
        var command = new AsyncCommand(
            async () =>
            {
                executionCount++;
                started.SetResult();
                await release.Task.ConfigureAwait(true);
            },
            _ => { });

        var firstExecution = command.ExecuteAsync();
        await started.Task.ConfigureAwait(true);
        await command.ExecuteAsync().ConfigureAwait(true);

        Assert.True(command.IsExecuting);
        Assert.False(command.CanExecute(parameter: null));
        Assert.Equal(1, executionCount);

        release.SetResult();
        await firstExecution.ConfigureAwait(true);

        Assert.False(command.IsExecuting);
        Assert.True(command.CanExecute(parameter: null));
    }

    [Fact]
    public async Task ExecuteAsyncReportsFailureThroughOwnerCallback()
    {
        var expectedFailure = new InvalidOperationException("Expected test failure.");
        Exception? reportedFailure = null;
        var command = new AsyncCommand(
            () => Task.FromException(expectedFailure),
            exception => reportedFailure = exception);

        await command.ExecuteAsync().ConfigureAwait(true);

        Assert.Same(expectedFailure, reportedFailure);
        Assert.False(command.IsExecuting);
    }

    [Fact]
    public async Task ExecuteAsyncAllowsOverlappingExecutionWhenRequested()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionCount = 0;
        var command = new AsyncCommand(
            () =>
            {
                executionCount++;
                return release.Task;
            },
            _ => { },
            allowConcurrentExecutions: true);

        var firstExecution = command.ExecuteAsync();
        var secondExecution = command.ExecuteAsync();

        Assert.Equal(2, executionCount);
        Assert.True(command.CanExecute(parameter: null));

        release.SetResult();
        await Task.WhenAll(firstExecution, secondExecution).ConfigureAwait(true);

        Assert.False(command.IsExecuting);
    }

    [Fact]
    public async Task EnabledStateChangesAtExecutionBoundaries()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var command = new AsyncCommand(
            () => release.Task,
            _ => { });
        var canExecuteChangedCount = 0;
        var executingChangedCount = 0;
        command.CanExecuteChanged += (_, _) => canExecuteChangedCount++;
        command.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(AsyncCommand.IsExecuting))
            {
                executingChangedCount++;
            }
        };

        var execution = command.ExecuteAsync();
        release.SetResult();
        await execution.ConfigureAwait(true);

        Assert.Equal(2, canExecuteChangedCount);
        Assert.Equal(2, executingChangedCount);
    }
}
