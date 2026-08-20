namespace Etiq.Editor.Core;

/// <summary>Dependency-free 2D primitives for the editor (user units).</summary>
public readonly record struct PointD(double X, double Y)
{
    public static PointD operator +(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);
    public static PointD operator -(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);
}

public readonly record struct RectD(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public PointD Center => new(X + W / 2, Y + H / 2);
    public bool Contains(PointD p) =>
        p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;
    public RectD Inflate(double d) => new(X - d, Y - d, W + 2 * d, H + 2 * d);
    public static RectD FromCorners(PointD a, PointD b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}

/// <summary>The eight resize handles + rotation, in the order the shell
/// will render them.</summary>
public enum Handle { NW, N, NE, E, SE, S, SW, W, Rotate }

public static class Geometry
{
    /// <summary>Rotate point p by `deg` degrees around `center` (SVG sense:
    /// positive = clockwise in a y-down coordinate system).</summary>
    public static PointD Rotate(PointD p, double deg, PointD center)
    {
        double rad = deg * Math.PI / 180.0;
        double c = Math.Cos(rad), s = Math.Sin(rad);
        double dx = p.X - center.X, dy = p.Y - center.Y;
        return new(center.X + dx * c - dy * s, center.Y + dx * s + dy * c);
    }

    /// <summary>Compose "rotate by delta about P" AFTER "rotate by a1 about
    /// p1" into our single-rotation transform model. Any composition of two
    /// rotations is a rotation by (a1+delta) about a unique pivot - except
    /// when the angles cancel, where it degenerates to a pure translation.
    /// This is what keeps a GROUP's internal layout intact when rotating
    /// members that already carry their own rotations.</summary>
    public static (double Deg, PointD Pivot, PointD Translate, bool IsTranslation)
        ComposeRotation(double a1, PointD p1, double delta, PointD P)
    {
        double theta = (a1 + delta) % 360;
        if (theta < 0) theta += 360;
        // the composed map: M(x) = rot(delta,P)( rot(a1,p1)(x) )
        PointD M(PointD x) => Rotate(Rotate(x, a1, p1), delta, P);
        if (Math.Abs(theta) < 1e-9 || Math.Abs(theta - 360) < 1e-9)
        {
            var t = M(p1);
            return (0, default, new PointD(t.X - p1.X, t.Y - p1.Y), true);
        }
        // M is affine: M(x) = R_theta*x + c; the fixed point q solves
        // (I - R_theta)*q = c with c = M(0,0)
        var cvec = M(new PointD(0, 0));
        double rad = theta * Math.PI / 180.0;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double det = (1 - cos) * (1 - cos) + sin * sin;
        double qx = ((1 - cos) * cvec.X - sin * cvec.Y) / det;
        double qy = (sin * cvec.X + (1 - cos) * cvec.Y) / det;
        return (theta, new PointD(qx, qy), default, false);
    }

    /// <summary>Hit-test a possibly rotated rect: inverse-rotate the point
    /// into object space, then a plain rect test. `pad` grows the target
    /// (world units) so thin objects stay clickable.</summary>
    public static bool HitRotatedRect(PointD p, RectD rect, double angleDeg,
                                      PointD pivot, double pad = 0)
    {
        var local = Rotate(p, -angleDeg, pivot);
        return rect.Inflate(pad).Contains(local);
    }

    /// <summary>Distance from point to segment — line/handle hit-testing.</summary>
    public static double DistToSegment(PointD p, PointD a, PointD b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len2 = dx * dx + dy * dy;
        double t = len2 == 0 ? 0 :
            Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2, 0, 1);
        double px = a.X + t * dx - p.X, py = a.Y + t * dy - p.Y;
        return Math.Sqrt(px * px + py * py);
    }

    /// <summary>Handle anchor positions for a rect in object space (before
    /// the object's own rotation is applied). Rotate handle floats above N.</summary>
    public static PointD HandlePos(RectD r, Handle h, double rotateOffset = 0) => h switch
    {
        Handle.NW => new(r.X, r.Y),
        Handle.N => new(r.X + r.W / 2, r.Y),
        Handle.NE => new(r.Right, r.Y),
        Handle.E => new(r.Right, r.Y + r.H / 2),
        Handle.SE => new(r.Right, r.Bottom),
        Handle.S => new(r.X + r.W / 2, r.Bottom),
        Handle.SW => new(r.X, r.Bottom),
        Handle.W => new(r.X, r.Y + r.H / 2),
        Handle.Rotate => new(r.X + r.W / 2, r.Y - rotateOffset),
        _ => r.Center,
    };

    /// <summary>Which handle (if any) is under the cursor for a rotated
    /// object. Returns null when none within `radius` world units.</summary>
    public static Handle? HitHandle(PointD p, RectD rect, double angleDeg,
                                    PointD pivot, double radius, double rotateOffset = 0)
    {
        foreach (Handle h in Enum.GetValues<Handle>())
        {
            var world = Rotate(HandlePos(rect, h, rotateOffset), angleDeg, pivot);
            double dx = world.X - p.X, dy = world.Y - p.Y;
            if (dx * dx + dy * dy <= radius * radius) return h;
        }
        return null;
    }

    /// <summary>Apply a resize drag: move the given handle to `to` (object
    /// space), keeping the opposite edge/corner fixed. Enforces min size.</summary>
    public static RectD ResizeBy(RectD r, Handle h, PointD to, double min = 1)
    {
        double x1 = r.X, y1 = r.Y, x2 = r.Right, y2 = r.Bottom;
        if (h is Handle.NW or Handle.W or Handle.SW) x1 = Math.Min(to.X, x2 - min);
        if (h is Handle.NE or Handle.E or Handle.SE) x2 = Math.Max(to.X, x1 + min);
        if (h is Handle.NW or Handle.N or Handle.NE) y1 = Math.Min(to.Y, y2 - min);
        if (h is Handle.SW or Handle.S or Handle.SE) y2 = Math.Max(to.Y, y1 + min);
        return new(x1, y1, x2 - x1, y2 - y1);
    }

    /// <summary>Snap a value to the grid (grid ≤ 0 = no snapping).</summary>
    public static double Snap(double v, double grid) =>
        grid <= 0 ? v : Math.Round(v / grid) * grid;

    public static PointD Snap(PointD p, double grid) =>
        new(Snap(p.X, grid), Snap(p.Y, grid));
}
