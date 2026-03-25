using System.Globalization;
using System.Text.Json;
using AlignmentReforge.Console;
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

    case "generate-case2-fixtures":
        var c2baseline = engine.Reconstruct(vertices, autoClassifyUnknownHints: false);
        var c2sampled = AlignmentSampler.SampleCase2(c2baseline, interval, lead);
        FixtureIo.WriteCase2Fixtures(fixtureRoot, c2sampled, c2baseline, interval);
        Console.WriteLine($"Case 2 fixtures written to {fixtureRoot} (interval={interval:F1}m).");
        return 0;

    default:
        Console.Error.WriteLine("Commands: solve | verify | dump-plan | selfcheck-case2 | generate-case2-fixtures");
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
