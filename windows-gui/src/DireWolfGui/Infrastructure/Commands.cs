using System.Windows;
using System.Windows.Input;

namespace DireWolfGui.Infrastructure;

/// <summary>Synchronous command.</summary>
public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
}

/// <summary>
/// Asynchronous command: disabled while running, exceptions are reported to the user
/// instead of crashing the application.
/// </summary>
public sealed class AsyncCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    private bool _running;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public bool IsRunning => _running;

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            await execute(parameter);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Dialogs.Error("The operation failed.", ex.Message);
        }
        finally
        {
            _running = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}
