using System;
using System.Collections.Generic;
using System.Linq;
using MindMap.Models;

namespace MindMap.Services;

/// <summary>
/// Parses an indented bullet outline (the format Whimsical uses when you copy a mind map)
/// into a laid-out <see cref="MindMapDocument"/>. Hierarchy comes from indentation; lines
/// without a bullet are treated as wrapped continuations of the previous node.
/// </summary>
public static class OutlineImporter
{
    private sealed class Node
    {
        public string Text;
        public readonly List<Node> Children = new();
        public Node(string text) => Text = text;
    }

    private const double NodeWidth = 170;
    private const double NodeHeight = 46;
    private const double XSpacing = 230; // centre-to-centre, per depth level
    private const double RowHeight = 62; // vertical slot per leaf

    public static MindMapDocument? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        Node? root = null;
        Node? last = null;
        var stack = new List<(int indent, Node node)>();

        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var (indent, content, isBullet) = Analyze(raw);
            if (content.Length == 0) continue;

            if (root == null)
            {
                root = new Node(content);
                stack.Add((indent, root));
                last = root;
                continue;
            }

            if (!isBullet)
            {
                // Wrapped continuation of the previous node.
                if (last != null)
                    last.Text = (last.Text + " " + content).Trim();
                continue;
            }

            while (stack.Count > 1 && stack[^1].indent >= indent)
                stack.RemoveAt(stack.Count - 1);

            var parent = stack[^1].node;
            var node = new Node(content);
            parent.Children.Add(node);
            stack.Add((indent, node));
            last = node;
        }

        if (root == null) return null;
        return Build(root);
    }

    private static (int indent, string content, bool isBullet) Analyze(string raw)
    {
        int indent = 0;
        int i = 0;
        for (; i < raw.Length; i++)
        {
            if (raw[i] == ' ') indent += 1;
            else if (raw[i] == '\t') indent += 4;
            else break;
        }

        var rest = raw.Substring(i);
        bool isBullet = false;
        foreach (var marker in new[] { "- ", "* ", "• ", "-", "*", "•" })
        {
            if (rest.StartsWith(marker))
            {
                rest = rest.Substring(marker.Length);
                isBullet = true;
                break;
            }
        }

        return (indent, rest.Trim(), isBullet);
    }

    private static MindMapDocument Build(Node root)
    {
        var pos = new Dictionary<Node, (double x, double y)>();

        // Split the root's branches roughly in half: first group right, second group left.
        int rightCount = (root.Children.Count + 1) / 2;
        var right = root.Children.Take(rightCount).ToList();
        var left = root.Children.Skip(rightCount).ToList();

        var rightNodes = new List<Node>();
        double cursor = 0;
        foreach (var b in right) LayoutSide(b, 1, +1, pos, rightNodes, ref cursor);
        CenterVertically(rightNodes, pos);

        var leftNodes = new List<Node>();
        cursor = 0;
        foreach (var b in left) LayoutSide(b, 1, -1, pos, leftNodes, ref cursor);
        CenterVertically(leftNodes, pos);

        pos[root] = (0, 0);

        // Emit nodes/connections. Coordinates above are node centres; convert to top-left.
        var doc = new MindMapDocument();
        var map = new Dictionary<Node, MindMapNode>();
        foreach (var n in Flatten(root))
        {
            var (cx, cy) = pos[n];
            var mn = new MindMapNode
            {
                Text = n.Text,
                Width = NodeWidth,
                Height = NodeHeight,
                X = cx - NodeWidth / 2,
                Y = cy - NodeHeight / 2,
                Color = ReferenceEquals(n, root) ? "#4C6EF5" : "#FFFFFF",
            };
            map[n] = mn;
            doc.Nodes.Add(mn);
        }

        foreach (var n in Flatten(root))
            foreach (var c in n.Children)
                doc.Connections.Add(new MindMapConnection { FromId = map[n].Id, ToId = map[c].Id });

        return doc;
    }

    /// <summary>Tidy tree layout: X by depth, Y by leaf order (parents centred on children).</summary>
    private static double LayoutSide(Node n, int depth, int dir,
        Dictionary<Node, (double, double)> pos, List<Node> collected, ref double cursor)
    {
        collected.Add(n);
        double x = dir * depth * XSpacing;
        double y;

        if (n.Children.Count == 0)
        {
            y = cursor;
            cursor += RowHeight;
        }
        else
        {
            double first = 0, last = 0;
            for (int i = 0; i < n.Children.Count; i++)
            {
                double cy = LayoutSide(n.Children[i], depth + 1, dir, pos, collected, ref cursor);
                if (i == 0) first = cy;
                last = cy;
            }
            y = (first + last) / 2;
        }

        pos[n] = (x, y);
        return y;
    }

    private static void CenterVertically(List<Node> nodes, Dictionary<Node, (double x, double y)> pos)
    {
        if (nodes.Count == 0) return;
        double min = nodes.Min(n => pos[n].y);
        double max = nodes.Max(n => pos[n].y);
        double mid = (min + max) / 2;
        foreach (var n in nodes)
            pos[n] = (pos[n].x, pos[n].y - mid);
    }

    private static IEnumerable<Node> Flatten(Node root)
    {
        var stack = new Stack<Node>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            for (int i = n.Children.Count - 1; i >= 0; i--)
                stack.Push(n.Children[i]);
        }
    }
}
