using Client.Workspace.Domain;

namespace Client.Workspace.Transfer;

/// <summary>
/// Bounded line-based diff (Myers O(ND) greedy). Produces an ordered list of
/// <see cref="DiffLine"/> entries (equal/insert/delete). The work is bounded by
/// <see cref="ArchiveLimits.DiffLineBudget"/> per side and an edit-step budget;
/// if the edit distance exceeds the budget, computation aborts and the caller
/// falls back to a summary view rather than freezing the UI.
/// </summary>
public static class LineDiff
{
    // Maximum total edits (inserts + deletes) before we give up and fall back.
    private const int EditBudget = 4096;

    /// <summary>Compute a unified line diff. Returns null if the edit distance
    /// exceeds the budget (caller shows a summary instead). Inputs must already
    /// be under the line/byte budgets.</summary>
    public static List<DiffLine>? Compute(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        int n = a.Count, m = b.Count;
        // Trivial fast paths.
        if (n == 0 && m == 0)
            return new List<DiffLine> { new(DiffOp.Equal, "") };
        if (n == 0)
        {
            var r = new List<DiffLine>(m);
            foreach (var s in b) r.Add(new DiffLine(DiffOp.Insert, s));
            return r;
        }
        if (m == 0)
        {
            var r = new List<DiffLine>(n);
            foreach (var s in a) r.Add(new DiffLine(DiffOp.Delete, s));
            return r;
        }

        int max = n + m;
        int maxD = Math.Min(EditBudget, max);
        // V[k] = furthest x reached on diagonal k. Offset by max so indices in [-max, max].
        var v = new int[2 * max + 1];
        // Trace of V per edit distance, for backtracking.
        var trace = new int[maxD + 1][];
        Array.Fill(v, -1);
        v[max + 0] = 0; // diagonal 0 starts at x=0

        int foundD = -1;
        for (int d = 0; d <= maxD; d++)
        {
            var vsnap = new int[2 * max + 1];
            Array.Copy(v, vsnap, v.Length);
            trace[d] = vsnap;

            for (int k = -d; k <= d; k += 2)
            {
                int kIndex = max + k;
                int x;
                if (d == 0)
                {
                    x = 0;
                }
                else if (k == -d || (k != d && v[kIndex - 1] < v[kIndex + 1]))
                {
                    x = v[kIndex + 1]; // insertion (move down)
                }
                else
                {
                    x = v[kIndex - 1] + 1; // deletion (move right)
                }
                int y = x - k;
                while (x < n && y < m && a[x] == b[y]) { x++; y++; }
                v[kIndex] = x;
                if (x >= n && y >= m) { foundD = d; goto done; }
            }
        }
        done:
        if (foundD < 0) return null; // edit distance exceeded budget

        // Backtrack from (n, m) using the trace to emit the diff in reverse, then reverse it.
        var result = new List<DiffLine>(n + m);
        int px = n, py = m;
        for (int d = foundD; d > 0; d--)
        {
            var vsnap = trace[d];
            int k = px - py;
            int kIndex = max + k;
            int prevK;
            if (d == 0) { break; }
            if (k == -d || (k != d && vsnap[kIndex - 1] < vsnap[kIndex + 1]))
                prevK = k + 1; // came from insertion (down)
            else
                prevK = k - 1; // came from deletion (right)

            int prevX = vsnap[max + prevK];
            int prevY = prevX - prevK;
            // Diagonal (equal) moves between (prevX, prevY) and (px, py): emit a[x].
            while (px > prevX && py > prevY)
            {
                result.Add(new DiffLine(DiffOp.Equal, a[px - 1]));
                px--; py--;
            }
            if (d > 0)
            {
                if (px == prevX) // insertion: b[py-1]
                {
                    result.Add(new DiffLine(DiffOp.Insert, b[py - 1]));
                    py--;
                }
                else // deletion: a[px-1]
                {
                    result.Add(new DiffLine(DiffOp.Delete, a[px - 1]));
                    px--;
                }
            }
        }
        // Remaining leading diagonal (equals) at d=0.
        {
            var v0 = trace[0];
            while (px > 0 && py > 0 && px == py)
            {
                result.Add(new DiffLine(DiffOp.Equal, a[px - 1]));
                px--; py--;
            }
        }
        result.Reverse();
        return result;
    }
}
