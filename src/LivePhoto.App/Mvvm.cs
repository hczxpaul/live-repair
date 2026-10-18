using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace LivePhoto.App;

/// <summary>极简 MVVM 基类（约 30 行，替代引入整个 CommunityToolkit）。</summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected void Raise(params string[] names)
    {
        foreach (string n in names)
        {
            OnPropertyChanged(n);
        }
    }
}

/// <summary>同步命令。</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;
    private EventHandler? _canExecuteChanged;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    // 一并挂到 CommandManager：任何输入事件后 WPF 会自动重查 CanExecute，
    // 否则"开始修复"这类按钮的可用状态要等手动 Raise 才会刷新。
    public event EventHandler? CanExecuteChanged
    {
        add
        {
            _canExecuteChanged += value;
            CommandManager.RequerySuggested += value;
        }
        remove
        {
            _canExecuteChanged -= value;
            CommandManager.RequerySuggested -= value;
        }
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => _canExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>异步命令（带忙状态，避免重入）。</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _running;
    private EventHandler? _canExecuteChanged;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    // 同上：交给 CommandManager 自动重查，按钮状态才会跟着数据变。
    public event EventHandler? CanExecuteChanged
    {
        add
        {
            _canExecuteChanged += value;
            CommandManager.RequerySuggested += value;
        }
        remove
        {
            _canExecuteChanged -= value;
            CommandManager.RequerySuggested -= value;
        }
    }

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (_running)
        {
            return;
        }

        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => _canExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>窗口级状态（互斥，避免非法流转）。</summary>
public enum ShellState
{
    Empty,

    /// <summary>T0 枚举 + T1 头部探测中（只读 64KB/张，很快）。</summary>
    Scanning,

    /// <summary>探测完成，等待用户用设备/机型等条件框定范围，再决定是否付 T2 的代价。</summary>
    Probed,

    /// <summary>T2 全量分析或分析完成后的就绪状态。</summary>
    Ready,
    Repairing,
    Finished,
}
