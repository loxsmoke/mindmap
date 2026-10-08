using Avalonia;
using Avalonia.Media;
using MindMap.Controls;
using MindMap.Models;

namespace MindMap.Tests;

public sealed class MindMapEditorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectionColorOptionIncludesDescendantsAndUndoesTogether(bool includeChildren)
    {
        var editor = new MindMapEditor();
        editor.LoadDocument(OutlineDocument());
        var doc = editor.GetDocument();
        var alpha = doc.Nodes.Single(n => n.Text == "Alpha");
        var child = doc.Nodes.Single(n => n.Text == "Alpha child");
        var grandchild = editor.TestCreateChild(child.Id)!;
        var original = doc.Nodes.ToDictionary(n => n.Id, n => n.Color);
        editor.TestSelectOnly(alpha.Id);
        var changes = 0;
        editor.DocumentChanged += (_, _) => changes++;

        editor.SetSelectionColor("#E64980", includeChildren);

        foreach (var node in doc.Nodes)
        {
            var shouldChange = node.Id == alpha.Id ||
                (includeChildren && (node.Id == child.Id || node.Id == grandchild.Id));
            Assert.Equal(shouldChange ? "#E64980" : original[node.Id], node.Color);
        }
        Assert.Equal(1, changes);

        editor.Undo();

        Assert.All(editor.GetDocument().Nodes, node => Assert.Equal(original[node.Id], node.Color));
    }

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
    public void ReparentingNodeMovesItUnderNewParentAndRemovesOldParentLink()
    {
        var editor = new MindMapEditor();
        editor.LoadDocument(OutlineDocument());
        var doc = editor.GetDocument();
        var alphaChild = doc.Nodes.Single(n => n.Text == "Alpha child");
        var beta = doc.Nodes.Single(n => n.Text == "Beta");

        var changed = editor.TestReparentNode(alphaChild.Id, beta.Id);

        Assert.True(changed);
        Assert.DoesNotContain(doc.Connections, c => c.FromId != beta.Id && c.ToId == alphaChild.Id);
        Assert.Contains(doc.Connections, c => c.FromId == beta.Id && c.ToId == alphaChild.Id);
        Assert.True(alphaChild.X > beta.X);
    }

    [Fact]
    public void ReparentingNodeOntoDescendantIsRejected()
    {
        var editor = new MindMapEditor();
        editor.LoadDocument(OutlineDocument());
        var doc = editor.GetDocument();
        var alpha = doc.Nodes.Single(n => n.Text == "Alpha");
        var alphaChild = doc.Nodes.Single(n => n.Text == "Alpha child");

        var changed = editor.TestReparentNode(alpha.Id, alphaChild.Id);

        Assert.False(changed);
        Assert.Contains(doc.Connections, c => c.FromId == alpha.Id && c.ToId == alphaChild.Id);
    }

    [Fact]
    public void MovingNodeOnSameSidePreservesAllOtherPositions()
    {
        var editor = new MindMapEditor();
        editor.LoadDocument(OutlineDocument());
        var doc = editor.GetDocument();
        var node = doc.Nodes.Single(n => n.Text == "Alpha");
        var others = doc.Nodes.Where(n => n.Id != node.Id).Select(n => (n.Id, n.X, n.Y)).ToArray();
        double x = node.X + 25;
        double y = node.Y - 300;

        editor.TestMoveNodeAndResolveDrag(node.Id, x, y);

        Assert.Equal(x, node.X);
        Assert.Equal(y, node.Y);
        Assert.Equal(others, doc.Nodes.Where(n => n.Id != node.Id).Select(n => (n.Id, n.X, n.Y)).ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void PassingChildrenRebuildsDescendantsWithMovedParentAnchored(bool left, bool moveRoot)
    {
        var editor = new MindMapEditor();
        var root = editor.GetDocument().Nodes.Single();
        var branch = editor.TestCreateChild(root.Id, leftOfRoot: left)!;
        var child = editor.TestCreateChild(branch.Id)!;
        var grandchild = editor.TestCreateChild(child.Id)!;
        var node = moveRoot ? root : branch;
        var unrelated = editor.TestCreateChild(root.Id, leftOfRoot: !left)!;
        var unrelatedPosition = (unrelated.X, unrelated.Y);
        // Drop beyond all descendants, with no overlap and no root-side change.
        double x = left ? grandchild.X - 500 : grandchild.X + grandchild.Width + 500;
        double y = node.Y;

        Assert.True(editor.TestMoveNodeAndResolveDrag(node.Id, x, y));

        Assert.Equal((x, y), (node.X, node.Y));
        bool childrenOnLeft = moveRoot ? !left : left;
        Assert.True(childrenOnLeft ? child.X + child.Width < branch.X : child.X > branch.X + branch.Width);
        Assert.True(childrenOnLeft ? grandchild.X + grandchild.Width < child.X : grandchild.X > child.X + child.Width);
        if (!moveRoot) Assert.Equal(unrelatedPosition, (unrelated.X, unrelated.Y));
        else Assert.All(editor.GetDocument().Nodes.Where(n => n.Id != root.Id),
            n => Assert.True(childrenOnLeft ? n.X + n.Width < root.X : n.X > root.X + root.Width));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovingParentOnlyRepairsItsChildrenWhenNecessary(bool crossRoot)
    {
        var editor = new MindMapEditor();
        editor.LoadDocument(OutlineDocument());
        var doc = editor.GetDocument();
        var root = doc.Nodes.Single(n => n.Text == "Root");
        var node = doc.Nodes.Single(n => n.Text == "Alpha");
        var child = doc.Nodes.Single(n => n.Text == "Alpha child");
        var others = doc.Nodes.Where(n => n.Id != node.Id && n.Id != child.Id)
            .Select(n => (n.Id, n.X, n.Y)).ToArray();
        double x = crossRoot ? root.X - 400 : child.X;
        double y = child.Y;

        editor.TestMoveNodeAndResolveDrag(node.Id, x, y);

        Assert.Equal(x, node.X);
        Assert.Equal(y, node.Y);
        Assert.True(crossRoot ? child.X + child.Width < node.X : child.X > node.X + node.Width);
        Assert.Equal(others, doc.Nodes.Where(n => n.Id != node.Id && n.Id != child.Id)
            .Select(n => (n.Id, n.X, n.Y)).ToArray());
    }

    [Fact]
    public void DraggingRootBranchAcrossRootMovesBranchToOtherSide()
    {
        var editor = new MindMapEditor();
        editor.LoadDocument(OutlineDocument());
        var doc = editor.GetDocument();
        var root = doc.Nodes.Single(n => n.Text == "Root");
        var alpha = doc.Nodes.Single(n => n.Text == "Alpha");
        var alphaChild = doc.Nodes.Single(n => n.Text == "Alpha child");

        var changed = editor.TestMoveNodeAndResolveDrag(alpha.Id, root.X - 260, alpha.Y);

        Assert.True(changed);
        Assert.Contains(doc.Connections, c => c.FromId == root.Id && c.ToId == alpha.Id);
        Assert.True(alpha.CenterX < root.CenterX);
        Assert.True(alphaChild.CenterX < alpha.CenterX);
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
