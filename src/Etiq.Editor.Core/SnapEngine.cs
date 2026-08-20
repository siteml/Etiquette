namespace Etiq.Editor.Core;

/// <summary>One alignment guide to draw while a snap is active.</summary>
public readonly record struct SnapGuide(bool Vertical, double Pos);

/// <summary>
/// Element-to-element snapping for drag operations. Candidates are the
/// left/center/right (and top/middle/bottom) of every stationary object's
/// bounds plus the label edges and center. The nearest candidate within
/// tolerance wins per axis; the returned deltas nudge the moving bounds
/// into alignment and the guides say where to draw the alignment lines.
/// </summary>
public static class SnapEngine
{
    /// <summary>Compute the snap adjustment for `moving` against
    /// `others` (+ optional label rect). `tol` in world units. Returns
    /// (0,0,[]) when nothing is within tolerance.</summary>
    public static (double Dx, double Dy, List<SnapGuide> Guides) Adjust(
        RectD moving, IReadOnlyList<RectD> others, RectD? label, double tol)
    {
        var (xs, ys) = Candidates(others, label);

        double[] movingXs = { moving.X, moving.X + moving.W / 2, moving.Right };
        double[] movingYs = { moving.Y, moving.Y + moving.H / 2, moving.Bottom };

        var guides = new List<SnapGuide>();
        double dx = Best(movingXs, xs, tol, out double? gx);
        double dy = Best(movingYs, ys, tol, out double? gy);
        if (gx is double vx) guides.Add(new SnapGuide(true, vx));
        if (gy is double hy) guides.Add(new SnapGuide(false, hy));
        return (dx, dy, guides);
    }

    /// <summary>Snap a single point (a resize handle, a line endpoint) to
    /// the same edge/center candidates. snapX/snapY restrict the axes so an
    /// N/S resize never snaps sideways. Returns the (possibly) adjusted
    /// point plus the guides to draw.</summary>
    public static (PointD P, List<SnapGuide> Guides) SnapPoint(
        PointD p, IReadOnlyList<RectD> others, RectD? label, double tol,
        bool snapX = true, bool snapY = true)
    {
        var (xs, ys) = Candidates(others, label);
        var guides = new List<SnapGuide>();
        double nx = p.X, ny = p.Y;
        if (snapX)
        {
            nx += Best(new[] { p.X }, xs, tol, out double? gx);
            if (gx is double vx) guides.Add(new SnapGuide(true, vx));
        }
        if (snapY)
        {
            ny += Best(new[] { p.Y }, ys, tol, out double? gy);
            if (gy is double hy) guides.Add(new SnapGuide(false, hy));
        }
        return (new PointD(nx, ny), guides);
    }

    private static (List<double> Xs, List<double> Ys) Candidates(
        IReadOnlyList<RectD> others, RectD? label)
    {
        var xs = new List<double>();
        var ys = new List<double>();
        foreach (var r in others)
        {
            xs.Add(r.X); xs.Add(r.X + r.W / 2); xs.Add(r.Right);
            ys.Add(r.Y); ys.Add(r.Y + r.H / 2); ys.Add(r.Bottom);
        }
        if (label is RectD lb)
        {
            xs.Add(lb.X); xs.Add(lb.X + lb.W / 2); xs.Add(lb.Right);
            ys.Add(lb.Y); ys.Add(lb.Y + lb.H / 2); ys.Add(lb.Bottom);
        }
        return (xs, ys);
    }

    private static double Best(double[] movingEdges, List<double> targets,
                               double tol, out double? guide)
    {
        double bestDelta = 0, bestAbs = double.MaxValue;
        guide = null;
        foreach (var t in targets)
            foreach (var m in movingEdges)
            {
                double d = t - m;
                double a = Math.Abs(d);
                if (a <= tol && a < bestAbs)
                {
                    bestAbs = a; bestDelta = d; guide = t;
                }
            }
        return bestDelta;
    }
}
