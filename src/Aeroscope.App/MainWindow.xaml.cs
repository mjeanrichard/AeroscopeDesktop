using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Aeroscope.App.ViewModels;

namespace Aeroscope.App;

public partial class MainWindow : Window
{
    /// <summary>Opens the connection dialog (Scope menu, Ctrl+K, or double-clicking the status).</summary>
    public static readonly RoutedUICommand ShowConnection =
        new("_Connection...", nameof(ShowConnection), typeof(MainWindow), [new KeyGesture(Key.K, ModifierKeys.Control)]);

    /// <summary>Runs the scope's calibration after asking for a shorted input.</summary>
    public static readonly RoutedUICommand Calibrate = new("_Calibrate...", nameof(Calibrate), typeof(MainWindow));

    public static readonly RoutedUICommand ClearCalibration = new("C_lear calibration...", nameof(ClearCalibration), typeof(MainWindow));

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

    private void CanCalibrate(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _viewModel.CalibrateCommand.CanExecute(null);

    private void OnCalibrate(object sender, ExecutedRoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Clip the probe tip to its ground lead so the input is at 0 V. An open input picks up hum.\n\n" +
            "The scope then measures the zero offset of every range and stores it, replacing the previous calibration. " +
            "It takes about a second.",
            "Calibrate", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (answer == MessageBoxResult.OK)
        {
            _viewModel.CalibrateCommand.Execute(null);
        }
    }

    private void CanClearCalibration(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _viewModel.ClearCalibrationCommand.CanExecute(null);

    private void OnClearCalibration(object sender, ExecutedRoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            "Clear the stored calibration? The scope then shows its uncorrected zero offset (up to about 1 V at 10 V/div) until it is calibrated again.",
            "Clear calibration", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.OK)
        {
            _viewModel.ClearCalibrationCommand.Execute(null);
        }
    }

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

    /// <summary>Number entries: Enter applies the typed value, Escape goes back to the current one.</summary>
    private void OnEntryKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        if (e.Key == Key.Enter)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            box.SelectAll();
            e.Handled = true;
        }
    }

    /// <summary>Selects the whole value on entering a number box, so typing replaces it.</summary>
    private void OnEntryFocused(object sender, KeyboardFocusChangedEventArgs e) => ((TextBox)sender).SelectAll();

    protected override void OnClosing(CancelEventArgs e)
    {
        _progressTimer.Stop();
        _viewModel.Dispose();
        base.OnClosing(e);
    }
}
