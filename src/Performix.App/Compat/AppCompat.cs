// 链接的 WPF ViewModel 直接引用 PreySense.Frontend.App（SuppressAutoHide、
// App.Current.Dispatcher）。这里提供同名替身，调度转发到 WinUI 3 的 DispatcherQueue。

namespace PreySense.Frontend
{
    internal sealed class App
    {
        private static readonly App _current = new();
        public static App Current => _current;

        public static bool SuppressAutoHide { get; set; }

        public System.Windows.Threading.Dispatcher Dispatcher =>
            System.Windows.Threading.Dispatcher.CurrentDispatcher;
    }
}
