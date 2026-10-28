using System;
using PuppyMacro.Services;
using Xunit;

namespace PuppyMacro.Tests;

/// <summary>The integrity level check that tells when an app run as administrator is in front.</summary>
public sealed class AppWindowsTests
{
    [Fact]
    public void PuppyMacros_own_process_is_not_above_its_integrity() =>
        Assert.False(AppWindows.IsAboveOwnIntegrity((uint)Environment.ProcessId));

    [Fact]
    public void A_process_that_cannot_be_read_counts_as_not_above() =>
        Assert.False(AppWindows.IsAboveOwnIntegrity(uint.MaxValue - 2));
}
