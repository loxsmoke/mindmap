using Avalonia;
using Avalonia.Media;
using MindMap.Controls;
using MindMap.Models;

namespace MindMap.Tests;

public sealed class MindMapEditorTests
{
    [Fact]
    public void AddingChildrenOneAtATimeKeepsLayoutOrderedAndNonOverlapping()
    {
        var editor = new MindMapEditor();
        var root = editor.GetDocument().Nodes.Single();

        var first = editor.TestCreateChild(root.Id)!;
        var second = editor.TestCreateChild(root.Id)!;
        var third = editor.TestCreateChild(root.Id)!;

        Assert.Equal(4, editor.GetDocument().Nodes.Count);
        Assert.Equal(3, editor.GetDocument().Connections.Count);
        Assert.All(new[] { first, second, third }, node => Assert.True(node.X > root.X));

        var children = editor.GetDocument().Connections
            .Where(c => c.FromId == root.Id)
            .Select(c => editor.GetDocument().Nodes.Single(n => n.Id == c.ToId))
            .OrderBy(n => n.Y)
            .ToList();

        Assert.Equal(new[] { first.Id, second.Id, third.Id }, children.Select(n => n.Id));
        for (var i = 1; i < children.Count; i++)
            Assert.True(children[i].Y >= children[i - 1].Y + children[i - 1].Height + 24 - 0.001);

        var branchMidpoint = (children.First().Y + children.Last().Y + children.Last().Height) / 2;
        Assert.InRange(branchMidpoint, root.CenterY - 0.001, root.CenterY + 0.001);
    }

    [Fact]
    public void RebuildLayoutIsStableWhenRunRepeatedly()
    {
        var editor = new MindMapEditor();
        var doc = OutlineDocument();
        foreach (var node in doc.Nodes)
        {
            node.X += node.Text.Length * 17;
            node.Y -= node.Text.Length * 11;
        }

        editor.LoadDocument(doc);
        editor.RebuildLayout();
        var firstLayout = Positions(editor.GetDocument());

        editor.RebuildLayout();
        var secondLayout = Positions(editor.GetDocument());

        Assert.Equal(firstLayout, secondLayout);
    }

    [Fact]
    public void BranchLineColorsAreAssignedByRootBranchAndInheritedByDescendants()
    {
        var editor = new MindMapEditor();
        editor.LoadDocument(OutlineDocument());

        var doc = editor.GetDocument();
        var alpha = doc.Nodes.Single(n => n.Text == "Alpha");
        var beta = doc.Nodes.Single(n => n.Text == "Beta");
        var alphaChild = doc.Nodes.Single(n => n.Text == "Alpha child");
        var colors = editor.TestBranchLineColors();

        Assert.Equal(MindMapEditor.PrimaryBranchColor, colors[alpha.Id]);
        Assert.Equal(MindMapEditor.PrimaryBranchColor, colors[alphaChild.Id]);
        Assert.Equal(MindMapEditor.DangerBranchColor, colors[beta.Id]);
        Assert.False(colors.ContainsKey(doc.Nodes.Single(n => n.Text == "Root").Id));
    }

    [Fact]
    public void EnsuringFocusedNodeVisiblePansViewportToContainIt()
    {
        var editor = new MindMapEditor
        {
            Width = 300,
            Height = 200,
        };
        editor.Measure(new Size(300, 200));
        editor.Arrange(new Rect(0, 0, 300, 200));

        var root = editor.GetDocument().Nodes.Single();
        var child = editor.TestCreateChild(root.Id)!;
        child.X = 900;
        child.Y = 500;
        var before = editor.TestPan;

        editor.TestEnsureNodeVisible(child.Id);
        var after = editor.TestPan;

        Assert.NotEqual(before, after);
        Assert.True(after.X < before.X);
        Assert.True(after.Y < before.Y);
    }

    [Fact]
    public void SelectAllNodesSelectsEveryNode()
    {
        var editor = new MindMapEditor();
        editor.LoadDocument(OutlineDocument());

        editor.SelectAllNodes();
        editor.DeleteSelection();

        Assert.Empty(editor.GetDocument().Nodes);
        Assert.Empty(editor.GetDocument().Connections);
    }

    [Fact]
    public void SelectAllWhileEditingSelectsNodeTextOnly()
    {
        var editor = new MindMapEditor();
        editor.LoadDocument(OutlineDocument());
        var root = editor.GetDocument().Nodes.Single(n => n.Text == "Root");
        editor.TestBeginEditNode(root.Id);

        var shouldFocusCanvas = editor.SelectAllForCurrentContext();

        Assert.False(shouldFocusCanvas);
        Assert.Equal((0, root.Text.Length), editor.TestEditorSelection);
        Assert.Equal(4, editor.GetDocument().Nodes.Count);
    }

    private static Dictionary<string, (double X, double Y)> Positions(MindMapDocument doc) =>
        doc.Nodes.ToDictionary(n => n.Id, n => (Math.Round(n.X, 6), Math.Round(n.Y, 6)));

    private static MindMapDocument OutlineDocument()
    {
        var root = new MindMapNode { Text = "Root", X = 400, Y = 300, Width = 170, Height = 54 };
        var alpha = new MindMapNode { Text = "Alpha", X = 700, Y = 250 };
        var alphaChild = new MindMapNode { Text = "Alpha child", X = 940, Y = 250 };
        var beta = new MindMapNode { Text = "Beta", X = 700, Y = 370 };

        return new MindMapDocument
        {
            Nodes = { root, alpha, alphaChild, beta },
            Connections =
            {
                new MindMapConnection { FromId = root.Id, ToId = alpha.Id },
                new MindMapConnection { FromId = alpha.Id, ToId = alphaChild.Id },
                new MindMapConnection { FromId = root.Id, ToId = beta.Id },
            },
        };
    }
}
