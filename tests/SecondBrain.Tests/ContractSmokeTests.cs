using FluentAssertions;
using SecondBrain.Core.Model;
using SecondBrain.Core.Ports;
using Xunit;

namespace SecondBrain.Tests;

public sealed class ContractSmokeTests
{
    [Fact]
    public void FrozenContracts_HaveExpectedDefaults()
    {
        NoteMetadata.CurrentSchemaVersion.Should().Be(2);
        var query = new SearchQuery("test");
        query.Limit.Should().Be(8);
    }
}