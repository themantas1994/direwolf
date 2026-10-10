using System.Text;

namespace DireWolfGui.Core.Config;

public enum DiffKind { Same, Removed, Added }

/// <summary>One line of a diff.  Line numbers are 1-based; 0 when the line is absent on that side.</summary>
public sealed record DiffLine(DiffKind Kind, string Text, int OldLineNumber, int NewLineNumber);

/// <summary>Line diff (Myers' O(ND) algorithm) for previewing configuration changes.</summary>
public static class TextDiff
{
    public static IReadOnlyList<DiffLine> Compute(string oldText, string newText) => Compute(SplitLines(oldText), SplitLines(newText));

    public static IReadOnlyList<DiffLine> Compute(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        // Common prefix and suffix first: typical edits touch a few lines.
        int pre = 0;
        while (pre < a.Count && pre < b.Count && a[pre] == b[pre]) pre++;
        int suf = 0;
        while (suf < a.Count - pre && suf < b.Count - pre && a[a.Count - 1 - suf] == b[b.Count - 1 - suf]) suf++;

        var result = new List<DiffLine>(a.Count + b.Count);
        for (int i = 0; i < pre; i++) result.Add(new DiffLine(DiffKind.Same, a[i], i + 1, i + 1));
        Myers(a, pre, a.Count - suf, b, pre, b.Count - suf, result);
        for (int i = 0; i < suf; i++)
        {
            int ai = a.Count - suf + i, bi = b.Count - suf + i;
            result.Add(new DiffLine(DiffKind.Same, a[ai], ai + 1, bi + 1));
        }
        return result;
    }

    private static void Myers(IReadOnlyList<string> a, int a0, int a1, IReadOnlyList<string> b, int b0, int b1, List<DiffLine> output)
    {
        int n = a1 - a0, m = b1 - b0, max = n + m;
        if (max == 0) return;
        int offset = max + 1;
        var v = new int[2 * max + 3];
        var trace = new List<int[]>();
        int found = -1;
        for (int d = 0; d <= max && found < 0; d++)
        {
            trace.Add((int[])v.Clone());
            for (int k = -d; k <= d; k += 2)
            {
                int x = k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]) ? v[offset + k + 1] : v[offset + k - 1] + 1;
                int y = x - k;
                while (x < n && y < m && a[a0 + x] == b[b0 + y]) { x++; y++; }
                v[offset + k] = x;
                if (x >= n && y >= m) { found = d; break; }
            }
        }
        // Backtrack.
        var ops = new List<DiffLine>();
        int cx = n, cy = m;
        for (int d = found; d > 0; d--)
        {
            var pv = trace[d];
            int k = cx - cy;
            int prevK = k == -d || (k != d && pv[offset + k - 1] < pv[offset + k + 1]) ? k + 1 : k - 1;
            int px = pv[offset + prevK], py = px - prevK;
            while (cx > px && cy > py) { cx--; cy--; ops.Add(new DiffLine(DiffKind.Same, a[a0 + cx], a0 + cx + 1, b0 + cy + 1)); }
            if (cx == px) { cy--; ops.Add(new DiffLine(DiffKind.Added, b[b0 + cy], 0, b0 + cy + 1)); }
            else { cx--; ops.Add(new DiffLine(DiffKind.Removed, a[a0 + cx], a0 + cx + 1, 0)); }
        }
        while (cx > 0 && cy > 0) { cx--; cy--; ops.Add(new DiffLine(DiffKind.Same, a[a0 + cx], a0 + cx + 1, b0 + cy + 1)); }
        ops.Reverse();
        output.AddRange(ops);
    }

    /// <summary>Split on CRLF, LF or CR.  A final line ending does not produce an extra empty line.</summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is '\r' or '\n')
            {
                lines.Add(text[start..i]);
                if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = i + 1;
            }
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    public static bool HasChanges(IReadOnlyList<DiffLine> diff) => diff.Any(d => d.Kind != DiffKind.Same);

    /// <summary>Unified diff text with <paramref name="context"/> lines around each change.</summary>
    public static string ToUnified(IReadOnlyList<DiffLine> diff, int context = 3, string oldName = "a", string newName = "b")
    {
        var sb = new StringBuilder();
        var changed = diff.Select((d, i) => (d, i)).Where(t => t.d.Kind != DiffKind.Same).Select(t => t.i).ToList();
        if (changed.Count == 0) return "";
        sb.Append("--- ").AppendLine(oldName).Append("+++ ").AppendLine(newName);
        int idx = 0;
        while (idx < changed.Count)
        {
            int start = Math.Max(0, changed[idx] - context), end = Math.Min(diff.Count - 1, changed[idx] + context);
            while (idx + 1 < changed.Count && changed[idx + 1] - context <= end + 1) { idx++; end = Math.Min(diff.Count - 1, changed[idx] + context); }
            idx++;
            var hunk = diff.Skip(start).Take(end - start + 1).ToList();
            int oldStart = hunk.FirstOrDefault(h => h.OldLineNumber > 0)?.OldLineNumber ?? 0;
            int newStart = hunk.FirstOrDefault(h => h.NewLineNumber > 0)?.NewLineNumber ?? 0;
            sb.Append($"@@ -{oldStart},{hunk.Count(h => h.Kind != DiffKind.Added)} +{newStart},{hunk.Count(h => h.Kind != DiffKind.Removed)} @@").AppendLine();
            foreach (var h in hunk) sb.Append(h.Kind switch { DiffKind.Added => '+', DiffKind.Removed => '-', _ => ' ' }).AppendLine(h.Text);
        }
        return sb.ToString();
    }
}
