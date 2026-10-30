using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Aeroscope.App.ViewModels;

namespace Aeroscope.App;

public partial class MainWindow : Window
{
    /// <summary>Opens the connection dialog (Scope menu, Ctrl+K, or double-clicking the status).</summary>
    public static readonly RoutedUICommand ShowConnection =
        new("_Connection...", nameof(ShowConnection), typeof(MainWindow), [new KeyGesture(Key.K, ModifierKeys.Control)]);

    private readonly MainViewModel _viewModel = new();
    private readonly DispatcherTimer _progressTimer;
    private bool _scrollPending;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // Moves the capture progress along between packets.
        _progressTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            (_, _) => _viewModel.Acquisition.UpdateProgress(), Dispatcher);

        _viewModel.Log.CollectionChanged += OnLogChanged;
        Loaded += (_, _) =>
        {
            _viewModel.Start();
            if (_viewModel.ShouldPickScope)
            {
                // Once the main window has drawn.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, ShowConnectionDialog);
            }
        };
    }

    private void ShowConnectionDialog() => new ConnectionWindow(_viewModel) { Owner = this }.ShowDialog();

    private void OnShowConnection(object sender, ExecutedRoutedEventArgs e) => ShowConnectionDialog();

    private void OnStatusDoubleClick(object sender, MouseButtonEventArgs e) => ShowConnectionDialog();

    /// <summary>
    /// Keeps the newest log line in view. Scrolling is deferred: calling ScrollIntoView
    /// while the collection is still raising its event can run before the ListBox has
    /// seen the new item, which throws "ItemsControl is inconsistent with its items source".
    /// </summary>
    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || _scrollPending)
        {
            return;
        }

        _scrollPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _scrollPending = false;
            if (LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
            }
        });
    }

    /// <summary>Wheel scrolls through the history (up is back in time), Shift+wheel in larger steps; Ctrl+wheel zooms the roll view.</summary>
    private void OnScreenMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var acquisition = _viewModel.Acquisition;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var zoom = e.Delta > 0 ? acquisition.ZoomInCommand : acquisition.ZoomOutCommand;
            if (zoom.CanExecute(null))
            {
                zoom.Execute(null);
            }
        }
        else
        {
            acquisition.Scroll(e.Delta > 0 ? -1 : 1, large: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        }

        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _progressTimer.Stop();
        _viewModel.Dispose();
        base.OnClosing(e);
    }
}
