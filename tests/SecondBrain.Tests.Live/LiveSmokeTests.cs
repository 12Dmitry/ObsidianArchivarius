using FluentAssertions;
using SecondBrain.Core.Model;
using Xunit;

namespace SecondBrain.Tests.Live;

public sealed class LiveSmokeTests
{
    [Fact]
    public void LiveProject_Compiles()
    {
        NoteMetadata.CurrentSchemaVersion.Should().Be(2);
    }
}