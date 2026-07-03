using System;
using System.Collections.Generic;
using System.Text;

namespace DeltaSharp.Plans;

/// <summary>
/// Shared renderer for the indented, multi-line tree string used by both the logical
/// <see cref="TreeNode{TNode}"/> and the physical plan tree (in <c>DeltaSharp.Executor</c>).
/// Centralizing the connector logic (<c>+- </c>/<c>:- </c>/<c>:  </c>/<c>   </c>) keeps the two layers'
/// rendering byte-identical, so a change to Spark's tree format cannot silently drift between them.
/// </summary>
internal static class TreeStringRenderer
{
    /// <summary>Renders <paramref name="root"/> and its descendants as an indented tree string.</summary>
    /// <typeparam name="T">The node type.</typeparam>
    /// <param name="root">The subtree root.</param>
    /// <param name="simpleString">Renders a node's own single-line label.</param>
    /// <param name="children">Returns a node's ordered children.</param>
    /// <returns>The rendered multi-line tree, one node per line, each terminated by <c>\n</c>.</returns>
    public static string Render<T>(
        T root, Func<T, string> simpleString, Func<T, IReadOnlyList<T>> children)
    {
        ArgumentNullException.ThrowIfNull(simpleString);
        ArgumentNullException.ThrowIfNull(children);
        var builder = new StringBuilder();
        Append(root, 0, new List<bool>(), builder, simpleString, children);
        return builder.ToString();
    }

    private static void Append<T>(
        T node, int depth, List<bool> lastChildFlags, StringBuilder builder,
        Func<T, string> simpleString, Func<T, IReadOnlyList<T>> children)
    {
        if (depth > 0)
        {
            for (int i = 0; i < lastChildFlags.Count - 1; i++)
            {
                builder.Append(lastChildFlags[i] ? "   " : ":  ");
            }

            builder.Append(lastChildFlags[^1] ? "+- " : ":- ");
        }

        builder.Append(simpleString(node));
        builder.Append('\n');

        IReadOnlyList<T> childNodes = children(node);
        for (int i = 0; i < childNodes.Count; i++)
        {
            lastChildFlags.Add(i == childNodes.Count - 1);
            Append(childNodes[i], depth + 1, lastChildFlags, builder, simpleString, children);
            lastChildFlags.RemoveAt(lastChildFlags.Count - 1);
        }
    }
}
