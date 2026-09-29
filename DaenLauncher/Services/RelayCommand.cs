using System.Windows.Input;

namespace DaenLauncher.Services;

/// <summary>
/// 简单命令实现（ICommand），用于托盘图标的单击/双击/右键命令绑定。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;

    public RelayCommand(Action execute)
    {
        _execute = execute;
    }

    // 命令始终可执行，无需通知机制（显式空实现避免 CS0067）
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
