using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Globalization;
using AlignmentReforge.Domain;
using AlignmentReforge.Geometry;

var fixtureRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures"));
var verticesPath = Path.Combine(fixtureRoot, "case1-sample.vertices.json");
var expectedPath = Path.Combine(fixtureRoot, "case1-expected-results.json");

var command = args.FirstOrDefault()?.ToLowerInvariant() ?? "solve";
var autoClassify = args.Any(arg => string.Equals(arg, "--auto-classify", StringComparison.OrdinalIgnoreCase));
var case2 = args.Any(arg => string.Equals(arg, "--case2", StringComparison.OrdinalIgnoreCase));
var case1 = args.Any(arg => string.Equals(arg, "--case1", StringComparison.OrdinalIgnoreCase));
var interval = ReadDouble(args, "--interval", 20.0);
var lead = ReadDouble(args, "--lead", Math.Max(1.0, interval * 0.35));

if (!File.Exists(verticesPath))
{
    Console.Error.WriteLine($"Missing fixture: {verticesPath}");
    return 1;
}

var vertices = FixtureIo.LoadVertices(verticesPath);
if (autoClassify)
{
    vertices = vertices.Select(vertex => vertex with { Hint = VertexHint.Unknown }).ToArray();
}
var engine = new AlignmentReconstructionEngine();
var solved = case2
    ? engine.ReconstructCase2(vertices.Select(vertex => vertex with { Hint = VertexHint.Unknown }).ToArray())
    : case1
        ? engine.Reconstruct(vertices, autoClassifyUnknownHints: autoClassify)
        : autoClassify
            ? engine.ReconstructAuto(vertices)
            : engine.Reconstruct(vertices, autoClassifyUnknownHints: false);

switch (command)
{
    case "solve":
        PrintSummary(solved, autoClassify);
        return 0;

    case "verify":
        if (!File.Exists(expectedPath))
        {
            Console.Error.WriteLine($"Missing expected fixture: {expectedPath}");
            return 1;
        }

        var expected = FixtureIo.LoadExpected(expectedPath);
        var verification = Verifier.Verify(solved, expected);
        Console.WriteLine(verification.Message);
        return verification.Success ? 0 : 2;

    case "dump-plan":
        Console.WriteLine(JsonSerializer.Serialize(solved.BuildPlan, FixtureIo.JsonOptions));
        return 0;

    case "selfcheck-case2":
        var baseline = engine.Reconstruct(vertices, autoClassifyUnknownHints: false);
        var sampledVertices = AlignmentSampler.SampleCase2(baseline, interval, lead);
        var sampledSolved = engine.ReconstructCase2(sampledVertices);
        var selfcheck = Verifier.VerifyCase2Equivalent(baseline, sampledSolved, interval);
        Console.WriteLine(selfcheck.Message);
        return selfcheck.Success ? 0 : 2;

    default:
        Console.Error.WriteLine("Commands: solve | verify | dump-plan | selfcheck-case2");
        return 1;
}

static void PrintSummary(SolvedAlignment solved, bool autoClassify)
{
    Console.WriteLine($"Mode: {(autoClassify ? "auto-classify" : "fixture-hints")}");
    Console.WriteLine($"Case: {solved.CaseType}");
    Console.WriteLine($"Curves: {solved.Curves.Count}");
    Console.WriteLine($"Build elements: {solved.BuildPlan.Count}");
    Console.WriteLine();

    foreach (var curve in solved.Curves)
    {
        Console.WriteLine(
            $"C{curve.CurveNumber}: R={curve.Radius:F6}  Ls={curve.SpiralLength:F6}  A={curve.A:F6}  " +
            $"TS={curve.StaTS:F3}  SC={curve.StaSC:F3}  CS={curve.StaCS:F3}  ST={curve.StaST:F3}");
    }

    Console.WriteLine();
    foreach (var issue in solved.Validation.Issues)
    {
        Console.WriteLine($"[{issue.Severity}] {issue.Message}");
    }
}

static double ReadDouble(string[] args, string name, double fallback)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }
    }

    return fallback;
}

static class FixtureIo
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static IReadOnlyList<Vertex> LoadVertices(string path)
    {
        var doc = JsonSerializer.Deserialize<VertexFixtureDocument>(File.ReadAllText(path), JsonOptions)
                  ?? throw new InvalidOperationException("Could not deserialize vertices fixture.");

        return doc.Vertices
            .Select(item => new Vertex(item.Index, new Point2D(item.X, item.Y), ParseHint(item.Hint)))
            .ToArray();
    }

    public static ExpectedFixtureDocument LoadExpected(string path)
    {
        return JsonSerializer.Deserialize<ExpectedFixtureDocument>(File.ReadAllText(path), JsonOptions)
               ?? throw new InvalidOperationException("Could not deserialize expected fixture.");
    }

    private static VertexHint ParseHint(string hint)
    {
        return hint.ToLowerInvariant() switch
        {
            "tangent" => VertexHint.Tangent,
            "curve" => VertexHint.Curve,
            "gap" => VertexHint.Gap,
            _ => VertexHint.Unknown
        };
    }
}

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

        static double Dist(AlignmentReforge.Domain.Point2D a, AlignmentReforge.Domain.Point2D b)
            => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

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
                    + Dist(ordered[j - 1].ST.Position, ordered[j].TS.Position);
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

sealed record VertexFixtureDocument(List<VertexFixtureItem> Vertices);
sealed record VertexFixtureItem(int Index, double X, double Y, string Hint);
sealed record ExpectedFixtureDocument(int CurveCount, List<ExpectedCurveFixtureItem> Curves);
sealed record ExpectedCurveFixtureItem(
    int Id,
    int TsIndex,
    int ScIndex,
    int CsIndex,
    int StIndex,
    double Radius,
    double SpiralLength,
    double A,
    double DeltaDegrees,
    string Direction,
    double ThetaSDegrees,
    double ArcAngleDegrees,
    double ArcLength,
    double ChordResidual,
    double StaTs,
    double StaSc,
    double StaCs,
    double StaSt);
