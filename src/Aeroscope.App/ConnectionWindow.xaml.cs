using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Aeroscope.App.ViewModels;

namespace Aeroscope.App;

/// <summary>Picks a scope and connects to it; closes itself once connected.</summary>
public partial class ConnectionWindow : Window
{
    private readonly MainViewModel _viewModel;

    public ConnectionWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.ConnectionDialogOpened();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.State) && _viewModel.State == ConnectionState.Connected)
        {
            Close();
        }
    }

    private void OnDeviceDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.ConnectCommand.CanExecute(null))
        {
            _viewModel.ConnectCommand.Execute(null);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.ConnectionDialogClosed();
        base.OnClosed(e);
    }
}
