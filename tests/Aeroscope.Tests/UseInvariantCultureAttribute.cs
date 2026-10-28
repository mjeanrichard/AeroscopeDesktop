using System.Globalization;
using System.Reflection;
using Xunit.Sdk;

namespace Aeroscope.Tests;

/// <summary>Runs a test under the invariant culture so formatted numbers are predictable.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class UseInvariantCultureAttribute : BeforeAfterTestAttribute
{
    private CultureInfo? _previous;

    public override void Before(MethodInfo methodUnderTest)
    {
        _previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
    }

    public override void After(MethodInfo methodUnderTest)
    {
        CultureInfo.CurrentCulture = _previous!;
    }
}

/// <summary>Tests that redirect or write to the console must not run in parallel.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCollection
{
    public const string Name = "Console";
}
