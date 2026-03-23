using System;
using System.Collections.Generic;
using System.Linq;
using AlignmentReforge.Domain;
using static AlignmentReforge.Geometry.GeometryMath;

namespace AlignmentReforge.Geometry;

internal static class Case1Solver
{
    internal static (SolvedCurve? curve, InconclusiveZone? inconclusive) SolveZone(
        IReadOnlyList<Vertex>           all,
        IReadOnlyList<VertexAttributes> attrs,
        TransitionZone                  zone,
        SolverSettings                  s)
    {
        try
        {
            var curve = DoSolve(all, attrs, zone, s);
            return (curve, null);
        }
        catch (System.Exception ex)
        {
            var inc = new InconclusiveZone(
                zone.CurveNumber,
                ex.Message,
                all[zone.TsIndex],
                all[zone.StIndex]);
            return (null, inc);
        }
    }

    private static SolvedCurve DoSolve(
        IReadOnlyList<Vertex>           all,
        IReadOnlyList<VertexAttributes> attrs,
        TransitionZone                  zone,
        SolverSettings                  s)
    {
        var ts = all[zone.TsIndex];
        var sc = all[zone.ScIndex];
        var cs = all[zone.CsIndex];
        var st = all[zone.StIndex];

        // ── Step 1: read distances directly ───────────────────────────────
        // Civil 3D exports spiral vertices at equal arc-length stations.
        // distance(TS,SC) = Ls_in  exactly (arc length, not chord).
        // distance(CS,ST) = Ls_out exactly.
        // distance(SC,CS) = arc chord exactly.

        var lsIn     = ChainLength(zone.SpiralIn);
        var lsOut    = ChainLength(zone.SpiralOut);
        var arcChord = Distance(sc.Position, cs.Position);

        // ── Step 2: entry and exit bearings ───────────────────────────────
        var entryBearing = zone.EntryBearing;
        var exitBearing  = zone.ExitBearing;

        var deltaSign = Deflection(entryBearing, exitBearing);
        if (Math.Abs(deltaSign) < 1e-6)
            throw new InvalidOperationException("Total deflection is effectively zero — not a curve.");

        var dir      = deltaSign >= 0 ? TurnDirection.Right : TurnDirection.Left;
        var deltaDeg = Math.Abs(deltaSign);
        var deltaRad = Deg2Rad(deltaDeg);

        // ── Step 3: solve R ───────────────────────────────────────────────
        double radius;
        bool   isPureArc = lsIn < s.NearZeroDistance && lsOut < s.NearZeroDistance;

        if (isPureArc)
        {
            var sinHalf = Math.Sin(deltaRad / 2.0);
            if (sinHalf < 1e-12)
                throw new InvalidOperationException("Pure arc: sin(Δ/2) is zero.");
            radius = arcChord / (2.0 * sinHalf);
        }
        else
        {
            radius = SolveR(lsIn, lsOut, arcChord, deltaRad, s);
        }

        if (radius <= 0)
            throw new InvalidOperationException("Solved radius is non-positive.");

        // ── Step 4: derive all parameters ─────────────────────────────────
        var thetaInRad  = isPureArc ? 0.0 : lsIn  / (2.0 * radius);
        var thetaOutRad = isPureArc ? 0.0 : lsOut / (2.0 * radius);
        var arcAngleRad = deltaRad - thetaInRad - thetaOutRad;

        if (arcAngleRad < -1e-9)
            throw new InvalidOperationException(
                $"Arc angle is negative ({Rad2Deg(arcAngleRad):F6}°) — spirals overlap.");

        arcAngleRad = Math.Max(0.0, arcAngleRad);

        var arcLength    = arcAngleRad * radius;
        var aIn          = isPureArc ? 0.0 : Math.Sqrt(radius * lsIn);
        var aOut         = isPureArc ? 0.0 : Math.Sqrt(radius * lsOut);
        var lsSymm       = (lsIn + lsOut) / 2.0;
        var aSymm        = Math.Sqrt(radius * lsSymm);
        var thetaInDeg   = Rad2Deg(thetaInRad);
        var thetaOutDeg  = Rad2Deg(thetaOutRad);
        var thetaSymmDeg = (thetaInDeg + thetaOutDeg) / 2.0;
        var arcAngleDeg  = Rad2Deg(arcAngleRad);

        var chordExpected = isPureArc
            ? arcChord
            : 2.0 * radius * Math.Sin(arcAngleRad / 2.0);
        var chordResidual = arcChord - chordExpected;

        // ── Step 5: membership test ────────────────────────────────────────
        var residualsIn = isPureArc
            ? Array.Empty<SpiralResidual>()
            : MembershipTest(zone.SpiralIn, ts, entryBearing, (int)dir, radius, lsIn);

        var residualsOut = isPureArc
            ? Array.Empty<SpiralResidual>()
            : MembershipTest(zone.SpiralOut, st,
                NormDeg(exitBearing + 180.0),
                -(int)dir,
                radius, lsOut);

        var allResiduals = residualsIn.Concat(residualsOut).ToArray();
        var rmsM = allResiduals.Length == 0
            ? 0.0
            : Math.Sqrt(allResiduals.Average(r => r.Residual * r.Residual));

        if (rmsM * 1000.0 > 50.0)
            throw new InvalidOperationException(
                $"Membership test RMS = {rmsM * 1000.0:F1} mm — points do not lie on a standard Clothoid.");

        var center = ArcCenter(sc.Position, cs.Position, radius, dir);

        return new SolvedCurve(
            CurveNumber:         zone.CurveNumber,
            Direction:           dir,
            Radius:              radius,
            SpiralLength:        lsSymm,
            SpiralLengthIn:      lsIn,
            SpiralLengthOut:     lsOut,
            A:                   aSymm,
            AIn:                 aIn,
            AOut:                aOut,
            DeltaDegrees:        deltaDeg,
            ThetaSDegrees:       thetaSymmDeg,
            ThetaSInDegrees:     thetaInDeg,
            ThetaSOutDegrees:    thetaOutDeg,
            ArcAngleDegrees:     arcAngleDeg,
            ArcLength:           arcLength,
            EntryBearing:        entryBearing,
            ExitBearing:         exitBearing,
            TS:                  ts,
            SC:                  sc,
            CS:                  cs,
            ST:                  st,
            StaTS:               attrs[zone.TsIndex].Station,
            StaSC:               attrs[zone.ScIndex].Station,
            StaCS:               attrs[zone.CsIndex].Station,
            StaST:               attrs[zone.StIndex].Station,
            ChordActual:         arcChord,
            ChordExpected:       chordExpected,
            ChordResidual:       chordResidual,
            ArcCenter:           center,
            MembershipRmsM:      rmsM,
            MembershipResiduals: allResiduals);
    }

    // ── Closure equation root-find ─────────────────────────────────────────

    private static double SolveR(
        double lsIn, double lsOut, double chord, double deltaRad, SolverSettings s)
    {
        var rLo = chord / 2.0 + 1e-3;
        var rHi = Math.Max(rLo * 2.0, (lsIn + lsOut + chord) / deltaRad);
        while (F(rHi, lsIn, lsOut, chord, deltaRad) > 0)
            rHi *= 2.0;

        var R = (rLo + rHi) / 2.0;

        for (var i = 0; i < s.RMaxIterations; i++)
        {
            var f  = F(R, lsIn, lsOut, chord, deltaRad);
            var df = Df(R, lsIn, lsOut, chord);
            if (Math.Abs(df) < 1e-15) break;
            var step = -f / df;
            R += step;
            if (R <= rLo || R >= rHi)
                R = (rLo + rHi) / 2.0;
            else if (f > 0) rLo = R - step;
            else             rHi = R - step;
            if (Math.Abs(step) < s.RClosureTolerance) break;
        }

        return R;
    }

    private static double F(double R, double lsIn, double lsOut, double chord, double deltaRad)
    {
        var ratio = chord / (2.0 * R);
        if (ratio >= 1.0) return double.MaxValue;
        return (lsIn + lsOut) / (2.0 * R) + 2.0 * Math.Asin(ratio) - deltaRad;
    }

    private static double Df(double R, double lsIn, double lsOut, double chord)
    {
        var ratio   = chord / (2.0 * R);
        var dArcsin = -1.0 / (R * Math.Sqrt(Math.Max(1e-15, 1.0 - ratio * ratio)));
        return -(lsIn + lsOut) / (2.0 * R * R) + chord * dArcsin / R;
    }

    // ── Membership test ────────────────────────────────────────────────────

    private static SpiralResidual[] MembershipTest(
        IReadOnlyList<Vertex> vertices,
        Vertex                anchor,
        double                anchorBearingDeg,
        int                   sign,
        double                R,
        double                Ls)
    {
        var bRad = Deg2Rad(anchorBearingDeg);
        var tx   =  Math.Sin(bRad);
        var ty   =  Math.Cos(bRad);
        var px   =  ty * sign;
        var py   = -tx * sign;

        return vertices.Select(v =>
        {
            var dx       = v.X - anchor.X;
            var dy       = v.Y - anchor.Y;
            var sAlong   = dx * tx + dy * ty;
            var measured = dx * px + dy * py;
            var expected = SpiralOffset(sAlong, R, Ls);
            return new SpiralResidual(v.Index, sAlong, measured, expected, measured - expected);
        }).ToArray();
    }
    private static double ChainLength(IReadOnlyList<Vertex> vertices)
{
    var total = 0.0;
    for (var i = 1; i < vertices.Count; i++)
        total += Distance(vertices[i - 1].Position, vertices[i].Position);
    return total;
}
}
