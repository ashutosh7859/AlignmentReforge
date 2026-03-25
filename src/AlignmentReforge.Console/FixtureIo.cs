using System.Text.Json;
using AlignmentReforge.Domain;

namespace AlignmentReforge.Console;

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

    public static void WriteCase2Fixtures(
        string fixtureRoot,
        IReadOnlyList<Vertex> sampledVertices,
        SolvedAlignment baseline,
        double interval)
    {
        // Write sampled vertices
        var vertexDoc = new VertexFixtureDocument(
            sampledVertices.Select(v => new VertexFixtureItem(v.Index, v.X, v.Y, "unknown")).ToList());
        var verticesPath = Path.Combine(fixtureRoot, $"case2-sample.vertices.{interval:F0}m.json");
        File.WriteAllText(verticesPath, JsonSerializer.Serialize(vertexDoc, JsonOptions));

        // Write expected results derived from baseline (arc-length-corrected stations)
        var ordered = baseline.Curves.OrderBy(c => c.StaTS).ToArray();
        var arcTs = new double[ordered.Length];
        var arcSc = new double[ordered.Length];
        var arcCs = new double[ordered.Length];
        var arcSt = new double[ordered.Length];
        if (ordered.Length > 0)
        {
            arcTs[0] = ordered[0].StaTS;
            arcSc[0] = ordered[0].StaSC;
            arcCs[0] = arcSc[0] + ordered[0].ArcLength;
            arcSt[0] = arcCs[0] + ordered[0].SpiralLengthOut;
            for (var j = 1; j < ordered.Length; j++)
            {
                arcTs[j] = arcSt[j - 1] + ordered[j - 1].ST.Position.DistanceTo(ordered[j].TS.Position);
                arcSc[j] = arcTs[j] + ordered[j].SpiralLengthIn;
                arcCs[j] = arcSc[j] + ordered[j].ArcLength;
                arcSt[j] = arcCs[j] + ordered[j].SpiralLengthOut;
            }
        }

        var expectedCurves = ordered.Select((c, i) => new ExpectedCurveFixtureItem(
            c.CurveNumber, c.TS.Index, c.SC.Index, c.CS.Index, c.ST.Index,
            c.Radius, c.SpiralLength, c.A, c.DeltaDegrees,
            c.Direction == TurnDirection.Left ? "L" : "R",
            c.ThetaSDegrees, c.ArcAngleDegrees, c.ArcLength, c.ChordResidual,
            arcTs[i], arcSc[i], arcCs[i], arcSt[i])).ToList();

        var expectedDoc = new ExpectedFixtureDocument(ordered.Length, expectedCurves);
        var expectedPath = Path.Combine(fixtureRoot, $"case2-expected-results.{interval:F0}m.json");
        File.WriteAllText(expectedPath, JsonSerializer.Serialize(expectedDoc, JsonOptions));
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
