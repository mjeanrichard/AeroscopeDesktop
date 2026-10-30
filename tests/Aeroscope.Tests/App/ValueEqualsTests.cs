using System.Globalization;
using System.Windows.Data;
using Aeroscope.App.Controls;
using Aeroscope.Core.Protocol;

namespace Aeroscope.Tests.App;

public class ValueEqualsTests
{
    private readonly ValueEquals _converter = new();

    [Theory]
    [InlineData(TriggerEdge.Rising, TriggerEdge.Rising, true)]
    [InlineData(TriggerEdge.Falling, TriggerEdge.Rising, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void Checked_when_the_value_is_the_parameter(object value, object parameter, bool expected)
    {
        Assert.Equal(expected, _converter.Convert(value, typeof(bool?), parameter, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Checking_sets_the_value_to_the_parameter()
    {
        Assert.Equal(TriggerEdge.Both, _converter.ConvertBack(true, typeof(TriggerEdge), TriggerEdge.Both, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Unchecking_leaves_the_value_to_the_button_now_checked()
    {
        Assert.Same(Binding.DoNothing, _converter.ConvertBack(false, typeof(TriggerEdge), TriggerEdge.Both, CultureInfo.InvariantCulture));
    }
}
