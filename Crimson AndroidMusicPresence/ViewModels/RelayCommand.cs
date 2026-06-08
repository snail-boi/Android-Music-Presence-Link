using System;
using System.Windows.Input;

namespace musicpresense
{
    /// <summary>
    /// Minimal ICommand. A button in XAML binds to a RelayCommand on the ViewModel
    /// (Command="{Binding ApplyCommand}") instead of using a Click event handler in
    /// code-behind. The command wraps a plain method.
    ///
    /// CanExecute is optional. When supplied, WPF disables the bound control while it
    /// returns false. CommandManager.RequerySuggested makes WPF re-check it whenever the
    /// UI does something that might have changed the answer (focus moves, a key is
    /// pressed, etc.), so you rarely have to poke it by hand.
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => _execute();

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
