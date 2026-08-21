using System.ComponentModel;
using System.Windows.Input;

namespace Libreguard.Vpn.Linux.ViewModels;

public sealed class AsyncCommand(Func<CancellationToken, Task> execute, Func<bool>? canExecute = null) : ICommand, INotifyPropertyChanged
{
    private bool _isRunning;
    private CancellationTokenSource? _runningCts;

    public event EventHandler? CanExecuteChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning == value)
            {
                return;
            }

            _isRunning = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
        }
    }

    public bool CanExecute(object? parameter) => !IsRunning && (canExecute?.Invoke() ?? true);

    public void Cancel() => _runningCts?.Cancel();

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _runningCts = new CancellationTokenSource();
        IsRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute(_runningCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _runningCts.Dispose();
            _runningCts = null;
            IsRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncParameterCommand(Func<object?, CancellationToken, Task> execute, Func<object?, bool>? canExecute = null) : ICommand, INotifyPropertyChanged
{
    private bool _isRunning;
    private CancellationTokenSource? _runningCts;

    public event EventHandler? CanExecuteChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (_isRunning == value)
            {
                return;
            }

            _isRunning = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
        }
    }

    public bool CanExecute(object? parameter) => !IsRunning && (canExecute?.Invoke(parameter) ?? true);

    public void Cancel() => _runningCts?.Cancel();

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _runningCts = new CancellationTokenSource();
        IsRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute(parameter, _runningCts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _runningCts.Dispose();
            _runningCts = null;
            IsRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
