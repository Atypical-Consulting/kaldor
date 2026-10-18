using System.Text.Json;

namespace Kaldor.Tests;

public class DoctorTests
{
    [Theory]
    [InlineData("1.2.0", """{"versions":["1.0.0","1.2.0","1.3.0"]}""", "1.3.0")]
    [InlineData("1.3.0", """{"versions":["1.0.0","1.2.0","1.3.0"]}""", null)]
    [InlineData("1.3.0", """{"versions":["1.0.0","1.2.0"]}""", null)]
    [InlineData("1.3.0", """{"versions":["1.3.0","1.4.0-beta.1"]}""", null)]
    [InlineData("1.3.0", """{"versions":[]}""", null)]
    public void Finds_a_newer_stable_version(string running, string index, string? newer) =>
        Assert.Equal(newer, Commands.NewerPublished(running, index));

    [Fact]
    public void Malformed_index_throws_for_the_doctor_to_report() =>
        Assert.ThrowsAny<JsonException>(() => Commands.NewerPublished("1.3.0", "<html>"));
}
