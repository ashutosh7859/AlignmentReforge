using System;
using System.Collections.Generic;
using System.Linq;
using AlignmentReforge.Domain;

namespace AlignmentReforge.Geometry;

/// <summary>
/// Pure static geometry primitives — no state, no Autodesk references.
/// All bearings are surveying azimuths: degrees clockwise from north, [0, 360).
/// All angles in degrees unless the method name says Radians.
/// </summary>
internal static class GeometryMath
{
    internal const double TwoPi = 2.0 * Math.PI;

    // ── distance ─────────────────────────────────────────────────────────────

    internal static double Distance(Point2D a, Point2D b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ── bearing / azimuth ────────────────────────────────────────────────────

    /// <summary>Surveying azimuth from <paramref name="from"/> to <paramref name="to"/>, degrees [0,360).</summary>
    internal static double Azimuth(Point2D from, Point2D to)
        => NormDeg(90.0 - Rad2Deg(Math.Atan2(to.Y - from.Y, to.X - from.X)));

    /// <summary>
    /// Signed deflection angle from <paramref name="inBearing"/> to <paramref name="outBearing"/>.
    /// Positive = right turn, negative = left turn.  Range (-180, 180].
    /// </summary>
    internal static double Deflection(double inBearing, double outBearing)
    {
        var d = outBearing - inBearing;
        if (d >  180.0) d -= 360.0;
        if (d < -180.0) d += 360.0;
        return d;
    }

    // ── normalisers ───────────────────────────────────────────────────────────

    internal static double NormDeg(double d)
    {
        d %= 360.0;
        return d < 0 ? d + 360.0 : d;
    }

    internal static double NormRad(double r)
    {
        r %= TwoPi;
        return r < 0 ? r + TwoPi : r;
    }

    // ── unit conversions ──────────────────────────────────────────────────────

    internal static double Deg2Rad(double d) => d * Math.PI / 180.0;
    internal static double Rad2Deg(double r) => r * 180.0 / Math.PI;

    // ── arc centre ────────────────────────────────────────────────────────────

    /// <summary>
    /// Given the chord endpoints SC and CS and the radius, returns the arc centre
    /// on the correct side for the given turn direction.
    /// Returns null only when the chord exceeds 2R (impossible for valid geometry).
    /// </summary>
    internal static Point2D? ArcCenter(Point2D sc, Point2D cs, double radius, TurnDirection dir)
    {
        var chord = Distance(sc, cs);
        if (chord > 2.0 * radius + 1e-6) return null;   // geometry violation
        var half  = chord / 2.0;
        var h     = Math.Sqrt(Math.Max(0.0, radius * radius - half * half));
        var mx    = (sc.X + cs.X) / 2.0;
        var my    = (sc.Y + cs.Y) / 2.0;
        var dx    = cs.X - sc.X;
        var dy    = cs.Y - sc.Y;
        // perpendicular unit vector
        var px    = -dy / chord;
        var py    =  dx / chord;
        var c1    = new Point2D(mx + h * px, my + h * py);
        var c2    = new Point2D(mx - h * px, my - h * py);
        // cross product of chord × (centre - sc) tells which side
        var cross = dx * (c1.Y - sc.Y) - dy * (c1.X - sc.X);
        return (dir == TurnDirection.Left && cross > 0) ||
               (dir == TurnDirection.Right && cross < 0)
            ? c1 : c2;
    }

    // ── osculating curvature (exact 3-point formula) ──────────────────────────

    /// <summary>
    /// Signed curvature at p1 from the circumscribed circle of (p0, p1, p2).
    /// Sign: positive = right-hand turn when traversing p0→p1→p2.
    /// Returns 0 when any two points coincide or the three are collinear.
    /// </summary>
    internal static double OsculatingCurvature(Point2D p0, Point2D p1, Point2D p2)
    {
        var a = Distance(p0, p1);
        var b = Distance(p1, p2);
        var c = Distance(p0, p2);
        if (a < 1e-12 || b < 1e-12 || c < 1e-12) return 0.0;
        // signed cross product (p1-p0) × (p2-p1)
        var cross = (p1.X - p0.X) * (p2.Y - p1.Y) - (p1.Y - p0.Y) * (p2.X - p1.X);
        return -2.0 * cross / (a * b * c);   // κ = 2|cross|/(a·b·c), signed by cross
    }

    // ── spiral offset (exact Fresnel via series) ──────────────────────────────

    /// <summary>
    /// Exact lateral offset of a clothoid spiral point at arc-length <paramref name="s"/>
    /// from the TS, for a spiral of parameter Ls (total length) and radius R.
    /// Uses the series expansion accurate to better than 0.001 mm for s ≤ Ls.
    /// offset = s³/(6·R·Ls) − s⁷/(336·R³·Ls³) + s¹¹/(42240·R⁵·Ls⁵) − …
    /// </summary>
    internal static double SpiralOffset(double s, double R, double Ls)
    {
        if (s <= 0.0) return 0.0;
        var rl   = R * Ls;
        var s2   = s * s;
        var t    = s2 / rl;         // = s²/(R·Ls)  dimensionless ramp parameter
        // offset = (s³/(6RL)) · [1 - t²/56 + t⁴/7040 - t⁶/2661120 + …]
        var series = 1.0
                   - t * t / 56.0
                   + t * t * t * t / 7040.0
                   - t * t * t * t * t * t / 2661120.0;
        return s * s2 / (6.0 * rl) * series;
    }

    // ── tangent bearing of spiral at arc-length s ─────────────────────────────

    /// <summary>
    /// Bearing (degrees) of the tangent to a clothoid spiral at arc-length s from TS.
    /// entryBearing is the TS tangent direction.
    /// sign = +1 for right turn, -1 for left turn.
    /// </summary>
    internal static double SpiralBearing(double entryBearingDeg, int sign, double s, double R, double Ls)
        => NormDeg(entryBearingDeg + sign * Rad2Deg(s * s / (2.0 * R * Ls)));

    // ── linear regression (least squares, two variables) ─────────────────────

    internal static (bool ok, double slope, double intercept) FitLine(
        IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        if (x.Count != y.Count || x.Count < 2) return (false, 0, 0);
        var n  = x.Count;
        var sx = 0.0; var sy = 0.0; var sxx = 0.0; var sxy = 0.0;
        for (var i = 0; i < n; i++) { sx += x[i]; sy += y[i]; sxx += x[i]*x[i]; sxy += x[i]*y[i]; }
        var den = n * sxx - sx * sx;
        if (Math.Abs(den) < 1e-12) return (false, 0, 0);
        var slope = (n * sxy - sx * sy) / den;
        var intercept = (sy - slope * sx) / n;
        return (true, slope, intercept);
    }

    // ── median ───────────────────────────────────────────────────────────────

    internal static double Median(double[] values)
    {
        if (values.Length == 0) return 0.0;
        var sorted = values.OrderBy(v => v).ToArray();
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
