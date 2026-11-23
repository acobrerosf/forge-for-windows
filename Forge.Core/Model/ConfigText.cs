using System.Text.RegularExpressions;

namespace Forge.Core.Model;

public enum DiffKind { Same, Added, Removed, Gap, Note }

/// <summary>One line of a diff as the confirm dialog shows it. A gap stands for unchanged lines left out; a note is a remark, not a line.</summary>
public sealed record DiffLine(DiffKind Kind, string Text);

/// <summary>
/// Pure text work for the config editors: line endings on the way back from
/// the editor, masking an environment file's values, and the diff a save shows.
/// None of it logs or keeps what it is given.
/// </summary>
public static partial class ConfigText
{
    public const string Mask = "••••••••";

    // ---------------------------------------------------------- line endings

    public static bool UsesCrlf(string? text) => (text ?? "").Contains("\r\n", StringComparison.Ordinal);

    /// <summary>
    /// A WinUI TextBox hands back <c>\r</c> for every line break, pasted ones too. Fold every
    /// kind to <c>\n</c>, then use the ending the file came with, so a Windows edit never turns
    /// Forge's <c>\n</c> into <c>\r\n</c>.
    /// </summary>
    public static string FromEditor(string? editorText, string? original)
    {
        var lf = (editorText ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return UsesCrlf(original) ? lf.Replace("\n", "\r\n", StringComparison.Ordinal) : lf;
    }

    // ------------------------------------------------------------- masking

    [GeneratedRegex(@"^(\s*(?:export\s+)?[A-Za-z_][A-Za-z0-9_.\-]*\s*=)(.*)$")]
    private static partial Regex Assignment();

    /// <summary>
    /// <c>KEY=value</c> reads as <c>KEY=••••••••</c>, whatever the value's length. Comments,
    /// blank lines and empty values are left as they are: they hold nothing to hide.
    /// </summary>
    public static string MaskLine(string line)
    {
        var cr = line.EndsWith('\r');
        var body = cr ? line[..^1] : line;
        var m = Assignment().Match(body);
        if (!m.Success || m.Groups[2].Value.Trim() == "") return line;
        return m.Groups[1].Value + Mask + (cr ? "\r" : "");
    }

    public static string MaskEnv(string? text) => string.Join('\n', (text ?? "").Split('\n').Select(MaskLine));

    // ---------------------------------------------------------------- diff

    /// <summary>Past this many cells the middle is shown as replaced whole rather than matched line by line.</summary>
    public const long DiffCellCap = 4_000_000;

    private static (string[] Lines, bool FinalNewline) Lines(string? text)
    {
        var t = (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);
        var final = t.EndsWith('\n');
        if (final) t = t[..^1];
        return (t == "" && !final ? [] : t.Split('\n'), final);
    }

    /// <summary>
    /// What a save changes, line by line, with <paramref name="context"/> unchanged lines around
    /// each change and the rest folded into gaps. With <paramref name="maskContext"/> (the
    /// environment file) the unchanged lines are masked; the changed ones are what is being checked.
    /// </summary>
    public static IReadOnlyList<DiffLine> Diff(string? before, string? after, int context = 3, bool maskContext = false)
    {
        var (a, aFinal) = Lines(before);
        var (b, bFinal) = Lines(after);

        var prefix = 0;
        while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
        var suffix = 0;
        while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[^(suffix + 1)] == b[^(suffix + 1)]) suffix++;

        var ops = new List<DiffLine>();
        for (var i = 0; i < prefix; i++) ops.Add(new(DiffKind.Same, a[i]));
        ops.AddRange(Middle(a[prefix..(a.Length - suffix)], b[prefix..(b.Length - suffix)]));
        for (var i = a.Length - suffix; i < a.Length; i++) ops.Add(new(DiffKind.Same, a[i]));

        var shown = Fold(ops, context, maskContext);
        // From or to an empty file the lines say it all.
        if (aFinal != bFinal && a.Length > 0 && b.Length > 0)
            shown.Add(new(DiffKind.Note, bFinal ? "Adds a newline at the end of the file" : "Removes the newline at the end of the file"));
        if (shown.Count == 0 && (before ?? "") != (after ?? ""))
            shown.Add(new(DiffKind.Note, "Only the line endings change"));
        return shown;
    }

    /// <summary>The changed middle, matched by longest common subsequence; removals come before additions.</summary>
    private static List<DiffLine> Middle(string[] a, string[] b)
    {
        var ops = new List<DiffLine>();
        if ((long)a.Length * b.Length > DiffCellCap)
        {
            ops.AddRange(a.Select(l => new DiffLine(DiffKind.Removed, l)));
            ops.AddRange(b.Select(l => new DiffLine(DiffKind.Added, l)));
            return ops;
        }

        // lcs[i, j]: the longest common run of a[i..] and b[j..].
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        int x = 0, y = 0;
        while (x < a.Length || y < b.Length)
        {
            if (x < a.Length && y < b.Length && a[x] == b[y]) { ops.Add(new(DiffKind.Same, a[x])); x++; y++; }
            else if (y >= b.Length || (x < a.Length && lcs[x + 1, y] >= lcs[x, y + 1])) { ops.Add(new(DiffKind.Removed, a[x])); x++; }
            else { ops.Add(new(DiffKind.Added, b[y])); y++; }
        }
        return ops;
    }

    private static List<DiffLine> Fold(List<DiffLine> ops, int context, bool mask)
    {
        var keep = new bool[ops.Count];
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Kind == DiffKind.Same) continue;
            for (var k = Math.Max(0, i - context); k <= Math.Min(ops.Count - 1, i + context); k++) keep[k] = true;
        }

        var shown = new List<DiffLine>();
        var skipped = 0;
        void Gap()
        {
            if (skipped == 0) return;
            shown.Add(new(DiffKind.Gap, skipped == 1 ? "1 unchanged line" : $"{skipped} unchanged lines"));
            skipped = 0;
        }
        var anyChange = false;
        for (var i = 0; i < ops.Count; i++)
        {
            if (!keep[i]) { skipped++; continue; }
            Gap();
            var op = ops[i];
            if (op.Kind != DiffKind.Same) anyChange = true;
            shown.Add(op.Kind == DiffKind.Same && mask ? op with { Text = MaskLine(op.Text) } : op);
        }
        if (anyChange) Gap();
        return anyChange ? shown : [];
    }
}
