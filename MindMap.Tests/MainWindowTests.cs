namespace MindMap.Tests;

public sealed class MainWindowTests
{
    [Fact]
    public void BuildWindowTitleIncludesVersionWithoutDocumentName()
    {
        Assert.Equal("MindMap - 0.1.0", MainWindow.BuildWindowTitle("0.1.0"));
    }

    [Fact]
    public void BuildWindowTitleIncludesVersionAndDocumentName()
    {
        Assert.Equal("MindMap - 0.1.0 - example.mmap", MainWindow.BuildWindowTitle("0.1.0", "example.mmap"));
    }

    [Fact]
    public void BuildAboutTitleIncludesVersionNextToAppName()
    {
        Assert.Equal("MindMap v0.1.0", MainWindow.BuildAboutTitle("0.1.0"));
    }

    [Fact]
    public void FormatDisplayVersionUsesThreePartNumericVersion()
    {
        Assert.Equal("1.2.3", MainWindow.FormatDisplayVersion(new Version(1, 2, 3, 4)));
    }
}
