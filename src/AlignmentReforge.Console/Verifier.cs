using AlignmentReforge.Domain;

namespace AlignmentReforge.Console;

static class Verifier
{
    public static (bool Success, string Message) Verify(SolvedAlignment solved, ExpectedFixtureDocument expected)
    {
        var errors = new List<string>();
        if (solved.Curves.Count != expected.CurveCount)
        {
            errors.Add($"Curve count mismatch. Expected {expected.CurveCount}, actual {solved.Curves.Count}.");
        }

        foreach (var pair in solved.Curves.Zip(expected.Curves, (actual, exp) => (actual, exp)))
        {
            Compare(errors, $"C{pair.exp.Id}.tsIndex", pair.exp.TsIndex, pair.actual.TS.Index);
            Compare(errors, $"C{pair.exp.Id}.scIndex", pair.exp.ScIndex, pair.actual.SC.Index);
            Compare(errors, $"C{pair.exp.Id}.csIndex", pair.exp.CsIndex, pair.actual.CS.Index);
            Compare(errors, $"C{pair.exp.Id}.stIndex", pair.exp.StIndex, pair.actual.ST.Index);
            Compare(errors, $"C{pair.exp.Id}.radius", pair.exp.Radius, pair.actual.Radius, 1e-3);
            Compare(errors, $"C{pair.exp.Id}.spiralLength", pair.exp.SpiralLength, pair.actual.SpiralLength, 1e-3);
            Compare(errors, $"C{pair.exp.Id}.a", pair.exp.A, pair.actual.A, 1e-3);
            Compare(errors, $"C{pair.exp.Id}.delta", pair.exp.DeltaDegrees, pair.actual.DeltaDegrees, 1e-6);
            Compare(errors, $"C{pair.exp.Id}.thetaS", pair.exp.ThetaSDegrees, pair.actual.ThetaSDegrees, 1e-6);
            Compare(errors, $"C{pair.exp.Id}.arcAngle", pair.exp.ArcAngleDegrees, pair.actual.ArcAngleDegrees, 1e-5);
            Compare(errors, $"C{pair.exp.Id}.arcLength", pair.exp.ArcLength, pair.actual.ArcLength, 1e-3);
            Compare(errors, $"C{pair.exp.Id}.chordResidual", pair.exp.ChordResidual, pair.actual.ChordResidual, 1e-6);
            Compare(errors, $"C{pair.exp.Id}.staTS", pair.exp.StaTs, pair.actual.StaTS, 1e-3);
            Compare(errors, $"C{pair.exp.Id}.staSC", pair.exp.StaSc, pair.actual.StaSC, 1e-3);
            Compare(errors, $"C{pair.exp.Id}.staCS", pair.exp.StaCs, pair.actual.StaCS, 1e-3);
            Compare(errors, $"C{pair.exp.Id}.staST", pair.exp.StaSt, pair.actual.StaST, 1e-3);
        }

        return errors.Count == 0
            ? (true, "Verification passed.")
            : (false, "Verification failed:\n" + string.Join(Environment.NewLine, errors));
    }

    public static (bool Success, string Message) VerifyCase2Equivalent(
        SolvedAlignment baseline,
        SolvedAlignment sampled,
        double interval)
    {
        var errors = new List<string>();
        if (baseline.Curves.Count != sampled.Curves.Count)
        {
            errors.Add($"Curve count mismatch. Expected {baseline.Curves.Count}, actual {sampled.Curves.Count}.");
        }

        // Build arc-length-based station chain for the baseline curves.
        // Case 1 chord stations (StaCS, StaST) are systematically shorter than arc-length
        // because the SC→CS gap vertex stores a single chord, not the arc.
        // The Case 2 solver works in the sampled-vertex coordinate system, which uses
        // chord distances between dense sample points — equivalent to arc-length for
        // small intervals.  Compute arc-length-expected stations using the same chain
        // formula used by AlignmentSampler so the comparison is apples-to-apples.
        var arcTsExpected = new double[baseline.Curves.Count];
        var arcScExpected = new double[baseline.Curves.Count];
        var arcCsExpected = new double[baseline.Curves.Count];
        var arcStExpected = new double[baseline.Curves.Count];

        var ordered = baseline.Curves.OrderBy(c => c.StaTS).ToArray();
        if (ordered.Length > 0)
        {
            arcTsExpected[0] = ordered[0].StaTS;           // leading tangent: chord = arc
            arcScExpected[0] = ordered[0].StaSC;           // spiral-in chain: chord ≈ arc
            arcCsExpected[0] = arcScExpected[0] + ordered[0].ArcLength;
            arcStExpected[0] = arcCsExpected[0] + ordered[0].SpiralLengthOut;

            for (var j = 1; j < ordered.Length; j++)
            {
                // Inter-curve tangent is straight: arc-length = Euclidean distance.
                arcTsExpected[j] = arcStExpected[j - 1]
                    + ordered[j - 1].ST.Position.DistanceTo(ordered[j].TS.Position);
                arcScExpected[j] = arcTsExpected[j] + ordered[j].SpiralLengthIn;
                arcCsExpected[j] = arcScExpected[j] + ordered[j].ArcLength;
                arcStExpected[j] = arcCsExpected[j] + ordered[j].SpiralLengthOut;
            }
        }

        foreach (var (pair, idx) in baseline.Curves
            .Zip(sampled.Curves, (expected, actual) => (expected, actual))
            .Select((p, i) => (p, i)))
        {
            var staTol = Math.Max(5.0, interval * 0.50);
            Compare(errors, $"C{pair.expected.CurveNumber}.radius", pair.expected.Radius, pair.actual.Radius, Math.Max(2.0, pair.expected.Radius * 0.03));
            Compare(errors, $"C{pair.expected.CurveNumber}.spiralIn", pair.expected.SpiralLengthIn, pair.actual.SpiralLengthIn, Math.Max(5.0, interval * 0.40));
            Compare(errors, $"C{pair.expected.CurveNumber}.spiralOut", pair.expected.SpiralLengthOut, pair.actual.SpiralLengthOut, Math.Max(5.0, interval * 0.40));
            Compare(errors, $"C{pair.expected.CurveNumber}.aIn", pair.expected.AIn, pair.actual.AIn, Math.Max(5.0, interval * 0.50));
            Compare(errors, $"C{pair.expected.CurveNumber}.aOut", pair.expected.AOut, pair.actual.AOut, Math.Max(5.0, interval * 0.50));
            Compare(errors, $"C{pair.expected.CurveNumber}.delta", pair.expected.DeltaDegrees, pair.actual.DeltaDegrees, 0.5);
            Compare(errors, $"C{pair.expected.CurveNumber}.arcLength", pair.expected.ArcLength, pair.actual.ArcLength, Math.Max(5.0, interval * 0.40));
            // Compare arc-length-based stations (not chord-based Case 1 stations)
            Compare(errors, $"C{pair.expected.CurveNumber}.staTS", arcTsExpected[idx], pair.actual.StaTS, staTol);
            Compare(errors, $"C{pair.expected.CurveNumber}.staSC", arcScExpected[idx], pair.actual.StaSC, staTol);
            Compare(errors, $"C{pair.expected.CurveNumber}.staCS", arcCsExpected[idx], pair.actual.StaCS, staTol);
            Compare(errors, $"C{pair.expected.CurveNumber}.staST", arcStExpected[idx], pair.actual.StaST, staTol);
        }

        return errors.Count == 0
            ? (true, $"Case 2 self-check passed at {interval:F1} m interval.")
            : (false, "Case 2 self-check failed:\n" + string.Join(Environment.NewLine, errors));
    }

    private static void Compare(List<string> errors, string label, int expected, int actual)
    {
        if (expected != actual)
        {
            errors.Add($"{label}: expected {expected}, actual {actual}");
        }
    }

    private static void Compare(List<string> errors, string label, double expected, double actual, double tolerance)
    {
        if (Math.Abs(expected - actual) > tolerance)
        {
            errors.Add($"{label}: expected {expected:F12}, actual {actual:F12}, tolerance {tolerance}");
        }
    }
}
