using System.Collections.Concurrent;
using System.Globalization;
using Aeroscope.Core.Bluetooth;

namespace Aeroscope.Probe;

internal static class ScanCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var seconds = args.Length > 0 ? double.Parse(args[0], CultureInfo.InvariantCulture) : 5;
        var seen = new ConcurrentDictionary<ulong, ScopeAdvertisement>();

        using var scanner = new ScopeScanner();
        scanner.AdvertisementReceived += (_, ad) => seen.AddOrUpdate(ad.Address, ad, (_, old) => ad with
        {
            Name = string.IsNullOrEmpty(ad.Name) ? old.Name : ad.Name,
            AdvertisesScopeService = ad.AdvertisesScopeService || old.AdvertisesScopeService,
        });

        Console.WriteLine($"Scanning for {seconds} s...");
        scanner.Start();
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        scanner.Stop();

        foreach (var ad in seen.Values.OrderByDescending(a => a.AdvertisesScopeService).ThenByDescending(a => a.Rssi))
        {
            Console.WriteLine($"{(ad.AdvertisesScopeService ? '*' : ' ')} {BluetoothAddress.Format(ad.Address)}  {ad.Rssi,4} dBm  {ad.Name}");
        }

        Console.WriteLine($"{seen.Count} device(s), {seen.Values.Count(a => a.AdvertisesScopeService)} Aeroscope(s).");
        return 0;
    }

    /// <summary>Scans until the first device advertising the Aeroscope service shows up.</summary>
    public static async Task<ScopeAdvertisement> FindFirstScopeAsync(TimeSpan timeout)
    {
        var found = new TaskCompletionSource<ScopeAdvertisement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scanner = new ScopeScanner();
        scanner.AdvertisementReceived += (_, ad) =>
        {
            if (ad.AdvertisesScopeService)
            {
                found.TrySetResult(ad);
            }
        };

        scanner.Start();
        try
        {
            return await found.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"No Aeroscope found within {timeout.TotalSeconds} s. Is it switched on and not connected to another app?");
        }
        finally
        {
            scanner.Stop();
        }
    }
}
