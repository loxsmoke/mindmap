using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using MindMap.Models;

namespace MindMap.Services;

public static class MindMapHtmlExporter
{
    private const double Padding = 48;
    private const string ConnectionFallback = "#98A2B3";
    private const string LightNodeBorder = "#CED4DA";

    private static readonly string[] BranchLineColors =
    {
        "#4C6EF5", "#F03E3E", "#F59F00", "#37B24D",
        "#7048E8", "#1098AD", "#E64980", "#F76707",
    };

    public static string Export(MindMapDocument document, string title)
    {
        var bounds = DocumentBounds(document);
        var viewX = bounds.X - Padding;
        var viewY = bounds.Y - Padding;
        var viewWidth = Math.Max(1, bounds.Width + Padding * 2);
        var viewHeight = Math.Max(1, bounds.Height + Padding * 2);
        var safeTitle = Html(title);
        var branchColors = BuildBranchColors(document);

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\">");
        sb.AppendLine("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.AppendLine($"  <title>{safeTitle}</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine("    :root { color-scheme: light; font-family: Inter, Segoe UI, system-ui, sans-serif; }");
        sb.AppendLine("    body { margin: 0; background: #f0f2f5; color: #1c1e21; }");
        sb.AppendLine("    main { min-height: 100vh; padding: 24px; box-sizing: border-box; display: grid; grid-template-rows: auto 1fr; gap: 16px; }");
        sb.AppendLine("    h1 { margin: 0; font-size: 20px; font-weight: 650; }");
        sb.AppendLine("    .viewport { min-height: 0; overflow: auto; background: #fff; border: 1px solid #d0d5dd; border-radius: 8px; }");
        sb.AppendLine("    svg { display: block; width: max(100%, 960px); height: auto; background: #fff; }");
        sb.AppendLine("    .canvas { fill: #fff; }");
        sb.AppendLine("    .node-label { box-sizing: border-box; height: 100%; padding: 10px; display: flex; align-items: center; font-size: 14px; line-height: 1.25; overflow-wrap: anywhere; white-space: pre-wrap; }");
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
        sb.AppendLine("  <main>");
        sb.AppendLine($"    <h1>{safeTitle}</h1>");
        sb.AppendLine("    <div class=\"viewport\">");
        sb.AppendLine($"      <svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{Num(viewX)} {Num(viewY)} {Num(viewWidth)} {Num(viewHeight)}\" role=\"img\" aria-label=\"{safeTitle}\">");
        sb.AppendLine($"        <rect class=\"canvas\" x=\"{Num(viewX)}\" y=\"{Num(viewY)}\" width=\"{Num(viewWidth)}\" height=\"{Num(viewHeight)}\" />");
        AppendConnections(sb, document, branchColors);
        AppendNodes(sb, document);
        sb.AppendLine("      </svg>");
        sb.AppendLine("    </div>");
        sb.AppendLine("  </main>");
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
        return sb.ToString();
    }

    private static void AppendConnections(
        StringBuilder sb,
        MindMapDocument document,
        IReadOnlyDictionary<string, string> branchColors)
    {
        foreach (var connection in document.Connections)
        {
            var from = document.Nodes.FirstOrDefault(n => n.Id == connection.FromId);
            var to = document.Nodes.FirstOrDefault(n => n.Id == connection.ToId);
            if (from == null || to == null) continue;

            var toRight = to.CenterX >= from.CenterX;
            var startX = toRight ? from.X + from.Width : from.X;
            var startY = from.CenterY;
            var endX = toRight ? to.X : to.X + to.Width;
            var endY = to.CenterY;
            var offset = Math.Max(40, Math.Abs(endX - startX) * 0.5);
            var c1X = startX + (toRight ? offset : -offset);
            var c2X = endX + (toRight ? -offset : offset);
            var stroke = branchColors.TryGetValue(connection.ToId, out var color)
                ? color
                : ConnectionFallback;

            sb.Append("        <path d=\"");
            sb.Append($"M {Num(startX)} {Num(startY)} C {Num(c1X)} {Num(startY)}, {Num(c2X)} {Num(endY)}, {Num(endX)} {Num(endY)}");
            sb.AppendLine($"\" fill=\"none\" stroke=\"{stroke}\" stroke-width=\"2.5\" stroke-linecap=\"round\" />");
        }
    }

    private static void AppendNodes(StringBuilder sb, MindMapDocument document)
    {
        foreach (var node in document.Nodes)
        {
            var fill = NormalizeHex(node.Color, "#4C6EF5");
            var textColor = Luminance(fill) > 0.6 ? "#000000" : "#FFFFFF";
            var border = Luminance(fill) > 0.85 ? LightNodeBorder : fill;
            var textAlign = NormalizeTextAlign(node.TextAlignment);
            var justify = textAlign switch
            {
                "center" => "center",
                "right" => "flex-end",
                _ => "flex-start",
            };

            sb.AppendLine($"        <g id=\"node-{HtmlAttribute(node.Id)}\">");
            sb.AppendLine($"          <rect x=\"{Num(node.X)}\" y=\"{Num(node.Y)}\" width=\"{Num(node.Width)}\" height=\"{Num(node.Height)}\" rx=\"10\" ry=\"10\" fill=\"{fill}\" stroke=\"{border}\" stroke-width=\"1.5\" />");
            sb.AppendLine($"          <foreignObject x=\"{Num(node.X)}\" y=\"{Num(node.Y)}\" width=\"{Num(node.Width)}\" height=\"{Num(node.Height)}\">");
            sb.AppendLine($"            <div xmlns=\"http://www.w3.org/1999/xhtml\" class=\"node-label\" style=\"color: {textColor}; text-align: {textAlign}; justify-content: {justify};\">{Html(node.Text)}</div>");
            sb.AppendLine("          </foreignObject>");
            sb.AppendLine("        </g>");
        }
    }

    private static (double X, double Y, double Width, double Height) DocumentBounds(MindMapDocument document)
    {
        if (document.IsEmpty) return (0, 0, 1, 1);

        var minX = document.Nodes.Min(n => n.X);
        var minY = document.Nodes.Min(n => n.Y);
        var maxX = document.Nodes.Max(n => n.X + n.Width);
        var maxY = document.Nodes.Max(n => n.Y + n.Height);
        return (minX, minY, maxX - minX, maxY - minY);
    }

    private static IReadOnlyDictionary<string, string> BuildBranchColors(MindMapDocument document)
    {
        var result = new Dictionary<string, string>();
        var roots = document.Nodes
            .Where(n => document.Connections.All(c => c.ToId != n.Id))
            .Select(n => n.Id)
            .ToHashSet();

        var branchColorOf = new Dictionary<string, string>();
        var index = 0;
        foreach (var connection in document.Connections)
        {
            if (roots.Contains(connection.FromId) && !branchColorOf.ContainsKey(connection.ToId))
                branchColorOf[connection.ToId] = BranchLineColors[index++ % BranchLineColors.Length];
        }

        foreach (var node in document.Nodes)
        {
            var branch = FindBranchNode(document, node.Id, roots);
            if (branch != null && branchColorOf.TryGetValue(branch, out var color))
                result[node.Id] = color;
        }

        return result;
    }

    private static string? FindBranchNode(MindMapDocument document, string nodeId, HashSet<string> roots)
    {
        var current = nodeId;
        for (var i = 0; i < 10000; i++)
        {
            if (roots.Contains(current)) return null;
            var parent = document.Connections.FirstOrDefault(c => c.ToId == current);
            if (parent == null) return null;
            if (roots.Contains(parent.FromId)) return current;
            current = parent.FromId;
        }

        return null;
    }

    private static string NormalizeHex(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        value = value.Trim();
        if (value.Length == 7 && value[0] == '#' && value.Skip(1).All(Uri.IsHexDigit))
            return value.ToUpperInvariant();

        return fallback;
    }

    private static double Luminance(string hex)
    {
        var r = int.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var g = int.Parse(hex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var b = int.Parse(hex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
    }

    private static string NormalizeTextAlign(string? value) =>
        string.Equals(value, "Center", StringComparison.OrdinalIgnoreCase) ? "center" :
        string.Equals(value, "Right", StringComparison.OrdinalIgnoreCase) ? "right" :
        "left";

    private static string Html(string? text) => WebUtility.HtmlEncode(text ?? "");

    private static string HtmlAttribute(string? text) => Html(text).Replace("\"", "&quot;");

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
