#nullable enable
using System;
using System.Windows.Input;

namespace EasyRDP.Shared
{
    /// <summary>
    /// 通用 ICommand 实现（无参数），供各 UI 工程通过链接共享（避免客户端/服务端重复维护两份）。
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException("execute");
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter)
        {
            return _canExecute == null || _canExecute();
        }

        public void Execute(object? parameter)
        {
            _execute();
        }

        /// <summary>手动触发 CanExecuteChanged（用于命令可用性联动）。</summary>
        public void RaiseCanExecuteChanged()
        {
            var handler = CanExecuteChanged;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 带参数的通用 ICommand 实现：Execute/CanExecute 接收 CommandParameter。
    /// 用于列表/模板中的按钮（如服务端会话"踢出"、客户端缩略图卡片"连接/编辑/删除"）。
    /// </summary>
    /// <typeparam name="T">CommandParameter 的目标类型。</typeparam>
    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        private readonly Func<T, bool>? _canExecute;

        public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException("execute");
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter)
        {
            if (_canExecute == null)
                return true;
            return parameter is T value && _canExecute(value);
        }

        public void Execute(object? parameter)
        {
            if (parameter is T value)
                _execute(value);
        }

        /// <summary>手动触发 CanExecuteChanged（用于命令可用性联动）。</summary>
        public void RaiseCanExecuteChanged()
        {
            var handler = CanExecuteChanged;
            if (handler != null)
                handler(this, EventArgs.Empty);
        }
    }
}
