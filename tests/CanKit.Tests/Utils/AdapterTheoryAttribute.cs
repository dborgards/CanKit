using System;
using Xunit;

namespace CanKit.Tests.Utils;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AdapterTheoryAttribute : TheoryAttribute
{
    public AdapterTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CANKIT_TEST_ADAPTERS")))
        {
            Skip = "Set CANKIT_TEST_ADAPTERS to run adapter-specific tests.";
        }
    }
}
