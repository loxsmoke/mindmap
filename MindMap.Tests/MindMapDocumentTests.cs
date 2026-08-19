using MindMap.Models;

namespace MindMap.Tests;

public sealed class MindMapDocumentTests
{
    [Fact]
    public void IsEmptyIsTrueForDocumentWithoutNodes()
    {
        var doc = new MindMapDocument();

        Assert.True(doc.IsEmpty);
    }

    [Fact]
    public void IsEmptyIsFalseForDocumentWithNodes()
    {
        var doc = MindMapDocument.CreateStarter();

        Assert.False(doc.IsEmpty);
    }

    [Fact]
    public void IsPristineStarterIsTrueForEmptyDocument()
    {
        var doc = new MindMapDocument();

        Assert.True(doc.IsPristineStarter);
    }

    [Fact]
    public void IsPristineStarterIsTrueForDefaultStarterDocument()
    {
        var doc = MindMapDocument.CreateStarter();

        Assert.True(doc.IsPristineStarter);
    }

    [Fact]
    public void IsPristineStarterIsFalseWhenStarterTextChanges()
    {
        var doc = MindMapDocument.CreateStarter();
        doc.Nodes[0].Text = "Renamed";

        Assert.False(doc.IsPristineStarter);
    }

    [Fact]
    public void IsPristineStarterIsFalseWhenStarterHasConnections()
    {
        var doc = MindMapDocument.CreateStarter();
        var child = new MindMapNode { Text = "Child" };
        doc.Nodes.Add(child);
        doc.Connections.Add(new MindMapConnection { FromId = doc.Nodes[0].Id, ToId = child.Id });

        Assert.False(doc.IsPristineStarter);
    }
}
