using Aeroscope.App;
using Aeroscope.App.ViewModels;
using Aeroscope.Core.Bluetooth;

namespace Aeroscope.Tests.App;

public class ConnectionWindowTests
{
    private static void OnStaThread(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new InvalidOperationException("Failed on the UI thread", error);
        }
    }

    [Fact]
    public void Scans_while_open_and_closes_once_connected()
    {
        var scanner = new FakeScopeScanner();
        MainViewModel? viewModel = null;
        var closed = false;

        OnStaThread(() =>
        {
            // On the window's thread: the device list's view belongs to it.
            viewModel = new MainViewModel(scanner, _ => Task.FromResult<IScopeLink>(new FakeScopeLink()), action => action(), new FakePreferencesStore());
            var window = new ConnectionWindow(viewModel);
            window.Closed += (_, _) => closed = true;
            Assert.True(viewModel.IsScanning);

            scanner.RaiseAdvertisement(new ScopeAdvertisement(0xC6A7C8D11AB1, "Aeroscope", -68, AdvertisesScopeService: true));
            viewModel.SelectedDevice = viewModel.Devices.Single();
            viewModel.ConnectCommand.Execute(null);
        });

        Assert.True(closed);
        Assert.Equal(ConnectionState.Connected, viewModel!.State);
    }
}
