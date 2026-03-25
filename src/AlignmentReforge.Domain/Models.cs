using System.Collections.Generic;
using System.Linq;

namespace AlignmentReforge.Domain;

public enum VertexHint
{
    Unknown,
    Tangent,
    Curve,
    Gap
}

public enum DisplayTag
{
    Tangent,
    Spiral,
    Ts,
    Cs
}

public enum TurnDirection
{
    Left  = -1,
    Right =  1
}

public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}

public enum AlignmentInputCase
{
    Auto,
    Case1GeometryPoints,
    Case2CenterlineSamples
}

// ---------- geometry primitives ----------

public sealed record Point2D(double X, double Y)
{
    public double DistanceTo(Point2D other)
    {
        var dx = other.X - X;
        var dy = other.Y - Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

public sealed record Vertex(int Index, Point2D Position, VertexHint Hint)
{
    public double X => Position.X;
    public double Y => Position.Y;
}

// ---------- solver settings ----------

public sealed record SolverSettings(
    // Case 1
    double GapChordThreshold              = 80.0,   // chord >= this → arc-chord gap (SC→CS)
    double TangentDeflectionThreshold     = 0.05,   // deg: max deflection still "tangent"
    double CurveDeflectionThreshold       = 0.002,  // deg: min deflection to classify "curve"
    double RClosureTolerance              = 1e-9,   // m: root-find convergence on R
    int    RMaxIterations                 = 20,     // always converges in <8 for valid data
    double ChordResidualTolerance         = 0.001,  // m: arc chord verification
    double NearZeroDistance               = 0.001,  // m: treat as zero-length

    // Civil 3D safety
    double MinTangentLength               = 0.10,   // m: shorter → refuse to build
    double MinArcAngleDegrees             = 0.001,  // deg: smaller → spirals overlap

    // Case 2 — curvature trapezoid reader
    double CurvatureZeroTolerance         = 1e-7,   // 1/m: below this → tangent
    double PlateauUniformityTolerance     = 0.05,   // fraction: max spread in plateau κ
    double TrapezoidResidualWarnMm        = 2.0,    // mm: membership test RMS warn
    double TrapezoidResidualErrorMm       = 10.0,   // mm: membership test RMS error
    int    Case2MinVertices               = 7);

// ---------- per-vertex computed attributes ----------

public sealed record VertexAttributes(
    int        Index,
    double     Station,
    double     Bearing,
    double     IncomingChordLength,
    double     Deflection,
    DisplayTag DisplayTag);

// ---------- a detected curve zone ----------

public sealed record TransitionZone(
    int                    CurveNumber,
    int                    TsIndex,
    int                    ScIndex,
    int                    CsIndex,
    int                    StIndex,
    IReadOnlyList<Vertex>  SpiralIn,
    IReadOnlyList<Vertex>  SpiralOut,
    double                 EntryBearing,
    double                 ExitBearing,
    double                 StartStation,
    double                 EndStation);

// ---------- membership test result per spiral point ----------

public sealed record SpiralResidual(
    int    VertexIndex,
    double S,
    double MeasuredOffset,
    double ExpectedOffset,
    double Residual);

// ---------- validation ----------

public sealed record ValidationIssue(ValidationSeverity Severity, string Message);

public sealed record ValidationReport(IReadOnlyList<ValidationIssue> Issues)
{
    public bool HasErrors   => Issues.Any(i => i.Severity == ValidationSeverity.Error);
    public bool HasWarnings => Issues.Any(i => i.Severity == ValidationSeverity.Warning);
}

// ---------- build plan elements ----------

public abstract record AlignmentElementPlan(
    string   Kind,
    Point2D  Start,
    Point2D  End,
    double   StartStation,
    double   EndStation);

public sealed record TangentPlan(
    Point2D Start,
    Point2D End,
    double  StartStation,
    double  EndStation)
    : AlignmentElementPlan("Tangent", Start, End, StartStation, EndStation);

public sealed record SpiralPlan(
    Point2D      Start,
    Point2D      End,
    double       StartStation,
    double       EndStation,
    double       Length,
    double       Radius,
    TurnDirection Direction)
    : AlignmentElementPlan("Spiral", Start, End, StartStation, EndStation);

public sealed record ArcPlan(
    Point2D      Start,
    Point2D      End,
    double       StartStation,
    double       EndStation,
    Point2D      Center,
    double       Radius,
    double       ArcLength,
    TurnDirection Direction)
    : AlignmentElementPlan("Arc", Start, End, StartStation, EndStation);

// ---------- solved curve ----------

public sealed record SolvedCurve(
    int                         CurveNumber,
    TurnDirection               Direction,
    double                      Radius,
    double                      SpiralLength,       // symmetric average; use In/Out for asymmetric
    double                      SpiralLengthIn,
    double                      SpiralLengthOut,
    double                      A,                  // sqrt(R·Ls) symmetric
    double                      AIn,
    double                      AOut,
    double                      DeltaDegrees,
    double                      ThetaSDegrees,      // spiral angle (symmetric)
    double                      ThetaSInDegrees,
    double                      ThetaSOutDegrees,
    double                      ArcAngleDegrees,
    double                      ArcLength,
    double                      EntryBearing,
    double                      ExitBearing,
    Vertex                      TS,
    Vertex                      SC,
    Vertex                      CS,
    Vertex                      ST,
    double                      StaTS,
    double                      StaSC,
    double                      StaCS,
    double                      StaST,
    double                      ChordActual,
    double                      ChordExpected,
    double                      ChordResidual,
    Point2D?                    ArcCenter,
    double                      MembershipRmsM,     // metres — should be <1mm for Case1
    IReadOnlyList<SpiralResidual> MembershipResiduals,
    double                      CurvatureFitRmsMm = 0.0);   // Case 2 only

// ---------- zone-level inconclusive report ----------

public sealed record InconclusiveZone(
    int    ZoneIndex,
    string Reason,
    Vertex EntryTangentEnd,
    Vertex ExitTangentStart);

// ---------- top-level result ----------

public sealed record SolvedAlignment(
    AlignmentInputCase                  CaseType,
    IReadOnlyList<Vertex>               InputVertices,
    IReadOnlyList<Vertex>               ClassifiedVertices,
    IReadOnlyList<VertexAttributes>     Attributes,
    IReadOnlyList<TransitionZone>       Zones,
    IReadOnlyList<SolvedCurve>          Curves,
    IReadOnlyList<InconclusiveZone>     InconclusiveZones,
    IReadOnlyList<AlignmentElementPlan> BuildPlan,
    ValidationReport                    Validation);
