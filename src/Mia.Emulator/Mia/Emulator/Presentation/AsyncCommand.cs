// SPDX-License-Identifier: MIT

using System.Windows.Input;

namespace Mia.Emulator.Presentation;

internal sealed class AsyncCommand : ObservableObject, ICommand
{
    private readonly Func<Task> _execute;
    private readonly Action<Exception> _onFailure;
    private readonly Func<bool>? _canExecute;
    private readonly bool _allowConcurrentExecutions;
    private int _executionCount;

    public AsyncCommand(
        Func<Task> execute,
        Action<Exception> onFailure,
        Func<bool>? canExecute = null,
        bool allowConcurrentExecutions = false)
    {
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(onFailure);

        _execute = execute;
        _onFailure = onFailure;
        _canExecute = canExecute;
        _allowConcurrentExecutions = allowConcurrentExecutions;
    }

    public event EventHandler? CanExecuteChanged;

    public bool IsExecuting => Volatile.Read(ref _executionCount) != 0;

    public bool CanExecute(object? parameter) =>
        (_allowConcurrentExecutions || !IsExecuting) && (_canExecute?.Invoke() ?? true);

    public void Execute(object? parameter) => _ = ExecuteAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(parameter: null) || !TryBeginExecution())
        {
            return;
        }

        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (Exception exception) when (ReportFailure(exception))
        {
        }
        finally
        {
            EndExecution();
        }
    }

    public void NotifyCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);

    private bool TryBeginExecution()
    {
        return _allowConcurrentExecutions
            ? BeginConcurrentExecution()
            : TryBeginSingleExecution();
    }

    private bool BeginConcurrentExecution()
    {
        if (Interlocked.Increment(ref _executionCount) == 1)
        {
            NotifyExecutionStateChanged();
        }

        return true;
    }

    private bool TryBeginSingleExecution()
    {
        if (Interlocked.CompareExchange(ref _executionCount, 1, 0) != 0)
        {
            return false;
        }

        NotifyExecutionStateChanged();
        return true;
    }

    private void EndExecution()
    {
        if (Interlocked.Decrement(ref _executionCount) == 0)
        {
            NotifyExecutionStateChanged();
        }
    }

    private void NotifyExecutionStateChanged()
    {
        OnPropertyChanged(nameof(IsExecuting));
        NotifyCanExecuteChanged();
    }

    private bool ReportFailure(Exception exception)
    {
        _onFailure(exception);
        return true;
    }
}
