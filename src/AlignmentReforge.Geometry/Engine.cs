using System;
using System.Collections.Generic;
using System.Linq;
using AlignmentReforge.Domain;
using static AlignmentReforge.Geometry.GeometryMath;

namespace AlignmentReforge.Geometry;

/// <summary>
/// Top-level reconstruction engine.
/// Owns: vertex classification, zone detection, build-plan assembly,
/// validation, Civil 3D safety gate.
/// All computation delegated to Case1Solver or Case2Solver.
/// No Autodesk references anywhere in this assembly.
/// </summary>
public sealed class AlignmentReconstructionEngine
{
    private readonly SolverSettings _s;

    public AlignmentReconstructionEngine(SolverSettings? settings = null)
        => _s = settings ?? new SolverSettings();

    // ═════════════════════════════════════════════════════════════════════════
    //  Public API
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Case 1: vertices ARE the geometry points (TS, SC, CS, ST exact).
    /// autoClassify=true when hints are Unknown and must be inferred from data.
    /// </summary>
    public SolvedAlignment Reconstruct(
        IReadOnlyList<Vertex> vertices,
        bool autoClassifyUnknownHints = true)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        if (vertices.Count < 3)
            throw new ArgumentException("At least three vertices are required.", nameof(vertices));

        var classified = autoClassifyUnknownHints
            ? ClassifyVertices(vertices)
            : vertices.ToArray();

        var attrs  = ComputeAttributes(classified);
        var zones  = DetectZones(classified, attrs);
        return AssembleCase1(vertices, classified, attrs, zones);
    }

    /// <summary>
    /// Case 2: vertices are interval-sampled points along the alignment centreline.
    /// All hints are treated as Unknown; curvature trapezoid reader is used.
    /// </summary>
    public SolvedAlignment ReconstructCase2(IReadOnlyList<Vertex> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        if (vertices.Count < _s.Case2MinVertices)
            throw new ArgumentException(
                $"At least {_s.Case2MinVertices} vertices required for Case 2.", nameof(vertices));

        var neutral  = vertices.Select(v => v with { Hint = VertexHint.Tangent }).ToArray();
        var stations = ComputeStations(neutral);
        var results  = Case2Solver.SolveAll(neutral, stations, _s);

        var curves      = results.Where(r => r.curve != null).Select(r => r.curve!).ToList();
        var inconclusive= results.Where(r => r.inconclusive != null).Select(r => r.inconclusive!).ToList();

        // Build classified vertex array
        var classified = ClassifyFromCurves(neutral, stations, curves);
        var attrs      = ComputeAttributes(classified);

        // Build zones from solved curves (Case2 solver already has this info)
        var zones = BuildZonesFromCurves(curves, classified);

        var plan       = BuildPlan(classified, curves);
        var validation = BuildValidation(curves, inconclusive, plan, isCase2: true);

        return new SolvedAlignment(
            AlignmentInputCase.Case2CenterlineSamples,
            vertices.ToArray(),
            classified,
            attrs,
            zones,
            curves,
            inconclusive,
            plan,
            validation);
    }

    /// <summary>
    /// Auto-detect which case applies and reconstruct accordingly.
    /// </summary>
    public SolvedAlignment ReconstructAuto(IReadOnlyList<Vertex> vertices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        var detectedCase = DetectCase(vertices);
        return detectedCase == AlignmentInputCase.Case2CenterlineSamples
            ? ReconstructCase2(vertices)
            : Reconstruct(vertices, autoClassifyUnknownHints: true);
    }

    /// <summary>
    /// Heuristic: presence of any Gap hint → Case 1.
    /// Regular spacing across >75% of segments → Case 2.
    /// </summary>
    public AlignmentInputCase DetectCase(IReadOnlyList<Vertex> vertices)
    {
        if (vertices.Any(v => v.Hint == VertexHint.Gap))
            return AlignmentInputCase.Case1GeometryPoints;

        var chords = Enumerable.Range(1, vertices.Count - 1)
            .Select(i => Distance(vertices[i-1].Position, vertices[i].Position))
            .ToArray();
        if (chords.Length == 0) return AlignmentInputCase.Case1GeometryPoints;

        var median    = Median(chords);
        var tolerance = Math.Max(median * 0.15, _s.NearZeroDistance);
        var regular   = chords.Count(c => Math.Abs(c - median) <= tolerance) / (double)chords.Length;

        return regular >= 0.75
            ? AlignmentInputCase.Case2CenterlineSamples
            : AlignmentInputCase.Case1GeometryPoints;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Case 1 internals
    // ═════════════════════════════════════════════════════════════════════════

    private Vertex[] ClassifyVertices(IReadOnlyList<Vertex> input)
    {
        if (input.All(v => v.Hint != VertexHint.Unknown)) return input.ToArray();

        var result = input.ToArray();
        var n      = result.Length;

        // Pass 1: gap detection by chord length
        for (var i = 1; i < n; i++)
        {
            var chord = Distance(result[i-1].Position, result[i].Position);
            if (chord >= _s.GapChordThreshold)
                result[i] = result[i] with { Hint = VertexHint.Gap };
        }

        // Pass 2: classify remaining as Tangent or Curve by deflection
        for (var i = 1; i < n - 1; i++)
        {
            if (result[i].Hint == VertexHint.Gap) continue;
            var bIn  = Azimuth(result[i-1].Position, result[i].Position);
            var bOut = Azimuth(result[i].Position,   result[i+1].Position);
            var defl = Math.Abs(Deflection(bIn, bOut));
            result[i] = result[i] with
            {
                Hint = defl >= _s.CurveDeflectionThreshold
                    ? VertexHint.Curve
                    : VertexHint.Tangent
            };
        }

        result[0]     = result[0]     with { Hint = VertexHint.Tangent };
        result[n - 1] = result[n - 1] with { Hint = VertexHint.Tangent };

        return result;
    }

    private IReadOnlyList<VertexAttributes> ComputeAttributes(IReadOnlyList<Vertex> v)
    {
        var n      = v.Count;
        var sta    = ComputeStations(v);
        var attrs  = new VertexAttributes[n];

        // Bearing at vertex i: midpoint direction from i-1 to i+1
        // (avoids single-segment noise at spiral vertices)
        double Bear(int i) => i == 0
            ? Azimuth(v[0].Position, v[1].Position)
            : i == n - 1
                ? Azimuth(v[n-2].Position, v[n-1].Position)
                : Azimuth(v[i-1].Position, v[i+1].Position);

        double Defl(int i) => i == 0 || i == n - 1 ? 0.0 :
            Deflection(Azimuth(v[i-1].Position, v[i].Position),
                       Azimuth(v[i].Position,   v[i+1].Position));

        for (var i = 0; i < n; i++)
        {
            var chord = i == 0 ? 0.0 : Distance(v[i-1].Position, v[i].Position);
            attrs[i] = new VertexAttributes(
                v[i].Index, sta[i], Bear(i), chord, Defl(i),
                ResolveTag(v[i].Hint, Math.Abs(Defl(i))));
        }

        return attrs;
    }

    private double[] ComputeStations(IReadOnlyList<Vertex> v)
    {
        var st = new double[v.Count];
        for (var i = 1; i < v.Count; i++)
            st[i] = st[i-1] + Distance(v[i-1].Position, v[i].Position);
        return st;
    }

    private IReadOnlyList<TransitionZone> DetectZones(
        IReadOnlyList<Vertex> v, IReadOnlyList<VertexAttributes> attrs)
    {
        var zones = new List<TransitionZone>();

        // A zone is anchored by a Gap vertex (the SC→CS arc chord).
        for (var gapIdx = 1; gapIdx < v.Count - 1; gapIdx++)
        {
            if (v[gapIdx].Hint != VertexHint.Gap) continue;
            if (attrs[gapIdx].IncomingChordLength < _s.GapChordThreshold) continue;
            if (Math.Abs(attrs[gapIdx].Deflection) < _s.TangentDeflectionThreshold) continue;

            // scIdx = vertex just before the gap (last spiral-in vertex)
            var scIdx = gapIdx - 1;
            // csIdx = the gap vertex itself (first vertex of spiral-out side)
            var csIdx = gapIdx;

            // Walk backward from scIdx to find TS (last Tangent before spiral-in)
            var tsIdx = -1;
            for (var i = scIdx - 1; i >= 0; i--)
            {
                if (v[i].Hint == VertexHint.Tangent) { tsIdx = i; break; }
                if (v[i].Hint == VertexHint.Gap)     { tsIdx = i; break; }
            }
            if (tsIdx < 0) continue;

            // Walk forward from csIdx to find ST (first Tangent after spiral-out)
            var stIdx = -1;
            for (var i = csIdx + 1; i < v.Count; i++)
            {
                if (v[i].Hint == VertexHint.Tangent) { stIdx = i; break; }
                // Next gap with large chord = next curve's SC, so spiral-out ends just before it
                if (v[i].Hint == VertexHint.Gap &&
                    attrs[i].IncomingChordLength >= _s.GapChordThreshold)
                { stIdx = i - 1; break; }
            }
            if (stIdx < 0) stIdx = v.Count - 1;

            // Entry bearing: average azimuth of tangent segments before TS
            var entryBearing = AverageTangentBearing(v, tsIdx, forward: false);
            var exitBearing  = AverageTangentBearing(v, stIdx, forward: true);

            var spiralIn  = v.Skip(tsIdx).Take(scIdx - tsIdx + 1).ToArray();
            var spiralOut = v.Skip(csIdx).Take(stIdx - csIdx + 1).ToArray();

            zones.Add(new TransitionZone(
                zones.Count + 1,
                tsIdx, scIdx, csIdx, stIdx,
                spiralIn, spiralOut,
                entryBearing, exitBearing,
                attrs[tsIdx].Station, attrs[stIdx].Station));
        }

        return zones;
    }

    /// <summary>
    /// Azimuth of the single segment immediately adjacent to the anchor vertex.
    /// forward=false → segment arriving at anchor (entry tangent).
    /// forward=true  → segment departing from anchor (exit tangent).
    /// Uses circular-mean machinery so it can be extended to multiple segments
    /// if needed, but only one segment is used to avoid curve-adjacent bias.
    /// </summary>
    private static double AverageTangentBearing(
        IReadOnlyList<Vertex> v, int anchor, bool forward)
    {
        var sinSum = 0.0; var cosSum = 0.0; var cnt = 0;
        if (forward)
        {
            for (var i = anchor; i < Math.Min(v.Count - 1, anchor + 1); i++)
            {
                if (v[i].Hint == VertexHint.Curve || v[i].Hint == VertexHint.Gap) break;
                var b = Deg2Rad(Azimuth(v[i].Position, v[i+1].Position));
                sinSum += Math.Sin(b); cosSum += Math.Cos(b); cnt++;
            }
        }
        else
        {
            for (var i = anchor; i > Math.Max(0, anchor - 1); i--)
            {
                if (v[i].Hint == VertexHint.Curve) break;
                var b = Deg2Rad(Azimuth(v[i-1].Position, v[i].Position));
                sinSum += Math.Sin(b); cosSum += Math.Cos(b); cnt++;
            }
        }
        if (cnt == 0)
        {
            return forward
                ? Azimuth(v[anchor].Position, v[Math.Min(anchor + 1, v.Count - 1)].Position)
                : Azimuth(v[Math.Max(anchor - 1, 0)].Position, v[anchor].Position);
        }
        return NormDeg(Rad2Deg(Math.Atan2(sinSum / cnt, cosSum / cnt)));
    }

    private SolvedAlignment AssembleCase1(
        IReadOnlyList<Vertex>          input,
        IReadOnlyList<Vertex>          classified,
        IReadOnlyList<VertexAttributes> attrs,
        IReadOnlyList<TransitionZone>  zones)
    {
        var curves      = new List<SolvedCurve>();
        var inconclusive= new List<InconclusiveZone>();

        foreach (var zone in zones)
        {
            var (curve, inc) = Case1Solver.SolveZone(classified, attrs, zone, _s);
            if (curve != null) curves.Add(curve);
            if (inc   != null) inconclusive.Add(inc);
        }

        var plan       = BuildPlan(classified, curves);
        var validation = BuildValidation(curves, inconclusive, plan, isCase2: false);

        return new SolvedAlignment(
            AlignmentInputCase.Case1GeometryPoints,
            input.ToArray(),
            classified.ToArray(),
            attrs,
            zones,
            curves,
            inconclusive,
            plan,
            validation);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Build plan
    // ═════════════════════════════════════════════════════════════════════════

    private IReadOnlyList<AlignmentElementPlan> BuildPlan(
        IReadOnlyList<Vertex> v, IReadOnlyList<SolvedCurve> curves)
    {
        var plan    = new List<AlignmentElementPlan>();
        var ordered = curves.OrderBy(c => c.StaTS).ToArray();

        if (ordered.Length == 0)
        {
            var len = Distance(v[0].Position, v[^1].Position);
            plan.Add(new TangentPlan(v[0].Position, v[^1].Position, 0.0, len));
            return plan;
        }

        // Leading tangent
        if (Distance(v[0].Position, ordered[0].TS.Position) > _s.NearZeroDistance)
            plan.Add(new TangentPlan(v[0].Position, ordered[0].TS.Position, 0.0, ordered[0].StaTS));

        for (var i = 0; i < ordered.Length; i++)
        {
            var c = ordered[i];

            plan.Add(new SpiralPlan(c.TS.Position, c.SC.Position,
                c.StaTS, c.StaSC, c.SpiralLengthIn, c.Radius, c.Direction));

            if (c.ArcCenter != null && c.ArcLength > _s.NearZeroDistance)
                plan.Add(new ArcPlan(c.SC.Position, c.CS.Position,
                    c.StaSC, c.StaCS, c.ArcCenter, c.Radius, c.ArcLength, c.Direction));

            plan.Add(new SpiralPlan(c.CS.Position, c.ST.Position,
                c.StaCS, c.StaST, c.SpiralLengthOut, c.Radius, c.Direction));

            // Inter-curve or trailing tangent
            var nextTS  = i + 1 < ordered.Length ? ordered[i+1].TS.Position : v[^1].Position;
            var nextSta = i + 1 < ordered.Length ? ordered[i+1].StaTS
                : c.StaST + Distance(c.ST.Position, v[^1].Position);

            if (Distance(c.ST.Position, nextTS) > _s.NearZeroDistance)
                plan.Add(new TangentPlan(c.ST.Position, nextTS, c.StaST, nextSta));
        }

        return plan;
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Validation — Civil 3D safety gate
    // ═════════════════════════════════════════════════════════════════════════

    private ValidationReport BuildValidation(
        IReadOnlyList<SolvedCurve>     curves,
        IReadOnlyList<InconclusiveZone> inconclusive,
        IReadOnlyList<AlignmentElementPlan> plan,
        bool isCase2)
    {
        var issues = new List<ValidationIssue>();

        // ── Inconclusive zones ─────────────────────────────────────────────
        foreach (var inc in inconclusive)
            issues.Add(new ValidationIssue(ValidationSeverity.Error,
                $"Zone {inc.ZoneIndex} inconclusive: {inc.Reason}"));

        // ── Per-curve checks ───────────────────────────────────────────────
        var ordered = curves.OrderBy(c => c.StaTS).ToArray();
        foreach (var c in ordered)
        {
            var lbl = $"C{c.CurveNumber}";

            // Arc angle must be positive — spirals must not consume entire deflection
            if (c.ArcAngleDegrees <= _s.MinArcAngleDegrees)
                issues.Add(new ValidationIssue(ValidationSeverity.Error,
                    $"{lbl}: arc angle = {c.ArcAngleDegrees:F6}° — spirals overlap. " +
                    "Civil 3D cannot build this curve."));
            else
                issues.Add(new ValidationIssue(ValidationSeverity.Info,
                    $"{lbl}: arc angle = {c.ArcAngleDegrees:F6}°  OK"));

            // A-value must be positive
            if (c.AIn <= 0 || c.AOut <= 0)
                issues.Add(new ValidationIssue(ValidationSeverity.Error,
                    $"{lbl}: A-value is zero or negative. Civil 3D will divide by zero."));

            // Radius positive
            if (c.Radius <= 0)
                issues.Add(new ValidationIssue(ValidationSeverity.Error,
                    $"{lbl}: radius = {c.Radius:F3} m is non-positive."));

            // Station ordering
            if (!(c.StaTS < c.StaSC && c.StaSC < c.StaCS && c.StaCS < c.StaST))
                issues.Add(new ValidationIssue(ValidationSeverity.Error,
                    $"{lbl}: station order violated — " +
                    $"TS={c.StaTS:F3} SC={c.StaSC:F3} CS={c.StaCS:F3} ST={c.StaST:F3}"));

            // Arc chord residual
            var chordSev = Math.Abs(c.ChordResidual) <= _s.ChordResidualTolerance
                ? ValidationSeverity.Info : ValidationSeverity.Warning;
            issues.Add(new ValidationIssue(chordSev,
                $"{lbl}: arc chord residual = {c.ChordResidual * 1000.0:F3} mm"));

            // Membership test RMS
            var rmsMm = c.MembershipRmsM * 1000.0;
            var rmsSev = rmsMm < 1.0  ? ValidationSeverity.Info
                       : rmsMm < 10.0 ? ValidationSeverity.Warning
                       : ValidationSeverity.Error;
            issues.Add(new ValidationIssue(rmsSev,
                $"{lbl}: membership RMS = {rmsMm:F3} mm"));

            if (isCase2 && c.CurvatureFitRmsMm > 0)
            {
                var fitSev = c.CurvatureFitRmsMm < 2.0  ? ValidationSeverity.Info
                           : c.CurvatureFitRmsMm < 10.0 ? ValidationSeverity.Warning
                           : ValidationSeverity.Error;
                issues.Add(new ValidationIssue(fitSev,
                    $"{lbl}: curvature-fit RMS = {c.CurvatureFitRmsMm:F3} mm"));
            }

            issues.Add(new ValidationIssue(ValidationSeverity.Info,
                $"{lbl}: R={c.Radius:F4} m  Ls_in={c.SpiralLengthIn:F4} m  " +
                $"Ls_out={c.SpiralLengthOut:F4} m  A_in={c.AIn:F4}  A_out={c.AOut:F4}  " +
                $"Dir={c.Direction}"));
        }

        // ── Adjacent curve overlap check ───────────────────────────────────
        for (var i = 0; i < ordered.Length - 1; i++)
        {
            var cur  = ordered[i];
            var next = ordered[i + 1];
            var gap  = next.StaTS - cur.StaST;
            if (gap < 0)
                issues.Add(new ValidationIssue(ValidationSeverity.Error,
                    $"C{cur.CurveNumber}→C{next.CurveNumber}: curves overlap by {-gap:F3} m. " +
                    "Civil 3D cannot build overlapping entities."));
            else if (gap < _s.MinTangentLength)
                issues.Add(new ValidationIssue(ValidationSeverity.Error,
                    $"C{cur.CurveNumber}→C{next.CurveNumber}: tangent gap = {gap:F3} m < " +
                    $"{_s.MinTangentLength:F3} m minimum. AddFixedLine will fail."));
            else
                issues.Add(new ValidationIssue(ValidationSeverity.Info,
                    $"C{cur.CurveNumber}→C{next.CurveNumber}: tangent = {gap:F3} m  OK"));
        }

        // ── Leading / trailing tangent length check ────────────────────────
        var tangentPlans = plan.OfType<TangentPlan>().ToArray();
        foreach (var tp in tangentPlans)
        {
            var len = Distance(tp.Start, tp.End);
            if (len < _s.MinTangentLength && len > _s.NearZeroDistance)
                issues.Add(new ValidationIssue(ValidationSeverity.Error,
                    $"Tangent [{tp.StartStation:F0}–{tp.EndStation:F0}]: " +
                    $"length = {len:F3} m < {_s.MinTangentLength:F3} m. " +
                    "AddFixedLine will fail in Civil 3D."));
        }

        return new ValidationReport(issues);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Helpers shared between Case 1 and Case 2
    // ═════════════════════════════════════════════════════════════════════════

    private static Vertex[] ClassifyFromCurves(
        Vertex[] neutral, double[] stations, List<SolvedCurve> curves)
    {
        var result = neutral.ToArray();
        foreach (var c in curves)
            for (var i = 0; i < result.Length; i++)
                if (stations[i] > c.StaTS && stations[i] < c.StaST)
                    result[i] = result[i] with { Hint = VertexHint.Curve };
        return result;
    }

    private static IReadOnlyList<TransitionZone> BuildZonesFromCurves(
        List<SolvedCurve> curves, IReadOnlyList<Vertex> classified)
    {
        return curves.Select(c => new TransitionZone(
            c.CurveNumber,
            c.TS.Index, c.SC.Index, c.CS.Index, c.ST.Index,
            classified.Where(v => v.Index >= c.TS.Index && v.Index <= c.SC.Index).ToArray(),
            classified.Where(v => v.Index >= c.CS.Index && v.Index <= c.ST.Index).ToArray(),
            c.EntryBearing, c.ExitBearing,
            c.StaTS, c.StaST)).ToArray();
    }

    private static DisplayTag ResolveTag(VertexHint hint, double absDefl)
        => hint switch
        {
            VertexHint.Gap when absDefl < 0.05 => DisplayTag.Ts,
            VertexHint.Gap                     => DisplayTag.Cs,
            VertexHint.Curve                   => DisplayTag.Spiral,
            _                                  => DisplayTag.Tangent
        };
}
