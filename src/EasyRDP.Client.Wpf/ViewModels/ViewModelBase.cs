using System.ComponentModel;

namespace EasyRDP.Client.Wpf.ViewModels
{
    public abstract class ViewModelBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Set<T>(ref T field, T value, string propertyName)
        {
            if (!object.Equals(field, value)) { field = value; OnPropertyChanged(propertyName); }
        }

        protected void OnPropertyChanged(string propertyName)
        {
            var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly System.Action _execute;
        private readonly System.Func<bool> _canExecute;

        public RelayCommand(System.Action execute, System.Func<bool> canExecute = null)
        {
            _execute = execute; _canExecute = canExecute;
        }

        public bool CanExecute(object p) { return _canExecute == null || _canExecute(); }
        public event System.EventHandler CanExecuteChanged
        {
            add { System.Windows.Input.CommandManager.RequerySuggested += value; }
            remove { System.Windows.Input.CommandManager.RequerySuggested -= value; }
        }
        public void Execute(object p) { _execute(); }
    }
}
