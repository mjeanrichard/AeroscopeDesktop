using Aeroscope.Core.Bluetooth;

namespace Aeroscope.Tests.Core;

public class AeroscopeGattTests
{
    [Theory]
    // UUIDs reported by the real scope: the Bluetooth base UUID with 16-bit ids.
    [InlineData("00001235-0000-1000-8000-00805f9b34fb", AeroscopeGatt.ScopeData, "Scope Data")]
    [InlineData("00001236-0000-1000-8000-00805f9b34fb", AeroscopeGatt.ScopeIn, "Scope In")]
    [InlineData("00001237-0000-1000-8000-00805f9b34fb", AeroscopeGatt.ScopeState, "Scope State")]
    [InlineData("00001239-0000-1000-8000-00805f9b34fb", AeroscopeGatt.ScopeOut, "Scope Out")]
    // Vendor-base form, in case other firmware uses it.
    [InlineData("F9541235-91B3-BD9A-F077-80F2A6E57D00", AeroscopeGatt.ScopeData, "Scope Data")]
    public void Short_id_and_role(string uuid, ushort shortId, string role)
    {
        Assert.Equal(shortId, AeroscopeGatt.ShortId(Guid.Parse(uuid)));
        Assert.Equal(role, AeroscopeGatt.RoleOf(shortId));
    }

    [Fact]
    public void Service_uuid_from_spec()
    {
        Assert.Equal(Guid.Parse("F9541234-91B3-BD9A-F077-80F2A6E57D00"), AeroscopeGatt.ServiceUuid);
        Assert.Equal(0x1234, AeroscopeGatt.ShortId(AeroscopeGatt.ServiceUuid));
    }

    [Fact]
    public void Unknown_role()
    {
        Assert.Equal("Unknown", AeroscopeGatt.RoleOf(0x2A00));
    }
}

public class BluetoothAddressTests
{
    [Fact]
    public void Real_scope_address_round_trips()
    {
        const ulong address = 0xC6A7C8D11AB1;

        Assert.Equal("C6:A7:C8:D1:1A:B1", BluetoothAddress.Format(address));
        Assert.Equal(address, BluetoothAddress.Parse("C6:A7:C8:D1:1A:B1"));
    }

    [Fact]
    public void Parse_accepts_lower_case_and_dashes()
    {
        Assert.Equal(0xC6A7C8D11AB1UL, BluetoothAddress.Parse("c6-a7-c8-d1-1a-b1"));
    }

    [Fact]
    public void Format_pads_with_zeros()
    {
        Assert.Equal("00:00:00:00:00:01", BluetoothAddress.Format(1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("C6:A7:C8:D1:1A")]
    [InlineData("C6:A7:C8:D1:1A:B1:00")]
    [InlineData("C6:A7:C8:D1:1A:B")]
    [InlineData("C6:A7:C8:D1:1A:XY")]
    public void Parse_rejects_malformed(string text)
    {
        Assert.Throws<FormatException>(() => BluetoothAddress.Parse(text));
    }
}
