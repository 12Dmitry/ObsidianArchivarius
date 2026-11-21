using Xunit;
using FluentAssertions;

namespace SecondBrain.Tests;

public class CoreTests
{
    [Fact]
    public void SmokeTest()
    {
        true.Should().BeTrue();
    }
}
