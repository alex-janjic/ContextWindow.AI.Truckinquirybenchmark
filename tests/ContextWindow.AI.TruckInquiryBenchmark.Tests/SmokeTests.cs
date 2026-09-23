namespace ContextWindow.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void SampleIsSynthetic()
    {
        Assert.Contains("Fictional", Usage.Description, StringComparison.Ordinal);
    }
}
