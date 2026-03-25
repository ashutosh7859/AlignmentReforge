using AlignmentReforge.Domain;
using AlignmentReforge.Geometry;
#if CIVIL3D
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Civil;
using Autodesk.Civil.ApplicationServices;
using Autodesk.Civil.DatabaseServices;
using Autodesk.Civil.DatabaseServices.Styles;
#endif

namespace AlignmentReforge.Civil3D;

public static class Civil3DProjectStatus
{
    public const string MissingReferencesMessage =
        "Set Civil3DInstallRoot to the AutoCAD / Civil 3D 2026 install root to activate plugin commands.";
}

#if CIVIL3D
public sealed class AlignmentReforgeCommands
{
    // ── ALIGNMENTDRYRUN ─────────────────────────────────────────────────
    // Runs the full geometry solver, prints all parameters and validation
    // issues to the command line.  Touches NO Civil 3D geometry objects.

    [CommandMethod("ALIGNMENTDRYRUN")]
    public void DryRun()
    {
        var doc    = Application.DocumentManager.MdiActiveDocument;
        var editor = doc.Editor;

        var inputCase = PromptInputCase(editor);
        var vertices  = PromptVertices(editor, doc.Database);
        if (vertices is null) return;

        var engine = new AlignmentReconstructionEngine();
        var solved = SolveByCase(engine, vertices, inputCase);

        editor.WriteMessage($"\n─────────────────────────────────────────");
        editor.WriteMessage($"\nCase: {solved.CaseType}");
        editor.WriteMessage($"\nCurves solved: {solved.Curves.Count}");
        editor.WriteMessage($"\nInconclusive zones: {solved.InconclusiveZones.Count}");

        foreach (var c in solved.Curves)
            editor.WriteMessage(
                $"\nC{c.CurveNumber}: R={c.Radius:F4} m  " +
                $"Ls_in={c.SpiralLengthIn:F4} m  Ls_out={c.SpiralLengthOut:F4} m  " +
                $"A_in={c.AIn:F4}  A_out={c.AOut:F4}  Dir={c.Direction}" +
                $"\n         TS={c.StaTS:F3}  SC={c.StaSC:F3}  " +
                $"CS={c.StaCS:F3}  ST={c.StaST:F3}" +
                $"\n         Membership RMS={c.MembershipRmsM * 1000.0:F3} mm");

        foreach (var inc in solved.InconclusiveZones)
            editor.WriteMessage($"\n[INCONCLUSIVE] Zone {inc.ZoneIndex}: {inc.Reason}");

        editor.WriteMessage("\n─── Validation ───────────────────────────");
        foreach (var issue in solved.Validation.Issues)
            editor.WriteMessage($"\n[{issue.Severity}] {issue.Message}");

        if (solved.Validation.HasErrors)
            editor.WriteMessage("\n\n⚠  Errors present — run ALIGNMENTBUILD only after resolving.");
        else
            editor.WriteMessage("\n\n✓  All checks passed — safe to run ALIGNMENTBUILD.");

        editor.WriteMessage("\n─────────────────────────────────────────\n");
    }

    // ── ALIGNMENTBUILD ───────────────────────────────────────────────────
    // Pre-flight gate: re-runs solver and validates completely.
    // Opens a Civil 3D transaction ONLY if every check passes.
    // Any validation error → hard stop, clear message, no transaction.

    [CommandMethod("ALIGNMENTBUILD")]
    public void BuildAlignment()
    {
        var doc    = Application.DocumentManager.MdiActiveDocument;
        var editor = doc.Editor;
        var db     = doc.Database;

        var inputCase = PromptInputCase(editor);
        var vertices  = PromptVertices(editor, db);
        if (vertices is null) return;

        var engine = new AlignmentReconstructionEngine();
        var solved = SolveByCase(engine, vertices, inputCase);

        // ── Pre-flight gate ────────────────────────────────────────────────
        // Every error is reported; then we stop.  Civil 3D is never touched
        // until geometry is fully confirmed.

        if (solved.Validation.HasErrors)
        {
            editor.WriteMessage("\n⚠  Build aborted — geometry validation failed:");
            foreach (var issue in solved.Validation.Issues.Where(i => i.Severity == ValidationSeverity.Error))
                editor.WriteMessage($"\n   [ERROR] {issue.Message}");
            editor.WriteMessage("\nRun ALIGNMENTDRYRUN for full report. Fix before building.\n");
            return;
        }

        if (solved.Curves.Count == 0)
        {
            editor.WriteMessage("\nNo curves solved — nothing to build.\n");
            return;
        }

        // ── Split-alignment prompt when inconclusive zones exist ────────────

        if (solved.InconclusiveZones.Count > 0)
        {
            editor.WriteMessage(
                $"\n{solved.InconclusiveZones.Count} zone(s) could not be determined.");
            foreach (var inc in solved.InconclusiveZones)
                editor.WriteMessage($"\n  Zone {inc.ZoneIndex}: {inc.Reason}");

            var opt = new PromptKeywordOptions(
                "\nBuild successful zones only as a partial alignment? [Yes/No] <No>: ",
                "Yes No") { AllowNone = true };
            var kw = editor.GetKeywords(opt);
            if (kw.Status != PromptStatus.OK ||
                string.IsNullOrWhiteSpace(kw.StringResult) ||
                kw.StringResult == "No")
            {
                editor.WriteMessage("\nBuild cancelled.\n");
                return;
            }
        }

        // ── Open transaction — geometry is confirmed, Civil 3D is safe ─────

        using var lockDoc   = doc.LockDocument();
        using var transaction = db.TransactionManager.StartTransaction();

        try
        {
            var civilDoc  = CivilApplication.ActiveDocument;
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var alignName = $"REFORGE_{timestamp}";

            var styleId    = ResolveStyleName(civilDoc.Styles.AlignmentStyles, "Standard", "CASE1 Style");
            var labelSetId = ResolveStyleName(
                civilDoc.Styles.LabelSetStyles.AlignmentLabelSetStyles, "Standard", "CASE1 LabelSet");

            var alignId   = Alignment.Create(civilDoc, alignName, null, "0", styleId, labelSetId);
            var alignment = (Alignment)transaction.GetObject(alignId, OpenMode.ForWrite);

            BuildEntitiesFromSolution(alignment, solved, editor);

            transaction.Commit();
            editor.WriteMessage($"\n✓  Created alignment '{alignName}'.\n");
        }
        catch (System.Exception ex)
        {
            transaction.Abort();
            editor.WriteMessage($"\n✗  Build failed: {ex.Message}\n");
        }
    }

    // ── Build Civil 3D entities from confirmed solution ────────────────────

    private static void BuildEntitiesFromSolution(
        Alignment alignment, SolvedAlignment solved, Editor editor)
    {
        // Entities must be added in chainage order.
        // Pattern: FixedLine (tangent) → FreeSCS (curve) → FixedLine → FreeSCS → …
        // Civil 3D FreeSCS requires the two bounding entity IDs.

        var tangentPlans = solved.BuildPlan.OfType<TangentPlan>()
                                           .OrderBy(t => t.StartStation)
                                           .ToArray();

        var curvesOrdered = solved.Curves.OrderBy(c => c.StaTS).ToArray();

        if (tangentPlans.Length < curvesOrdered.Length + 1)
            throw new InvalidOperationException(
                $"Expected {curvesOrdered.Length + 1} tangent segments, found {tangentPlans.Length}. " +
                "Validation gate should have caught this.");

        // Add all tangent fixed lines first, record their entity IDs
        var tangentIds = new List<int>(tangentPlans.Length);
        foreach (var t in tangentPlans)
        {
            alignment.Entities.AddFixedLine(ToPoint3d(t.Start), ToPoint3d(t.End));
            tangentIds.Add(alignment.Entities.LastEntity);
        }

        // Add each curve as FreeSCS between its bounding tangents
        for (var i = 0; i < curvesOrdered.Length; i++)
        {
            var c        = curvesOrdered[i];
            var prevId   = tangentIds[i];
            var nextId   = tangentIds[i + 1];

            // Civil 3D uses A-value (parameter) not Ls directly
            // AIn = sqrt(R·Ls_in),  AOut = sqrt(R·Ls_out)
            alignment.Entities.AddFreeSCS(
                prevId, nextId,
                c.AIn, c.AOut,
                SpiralParamType.AValue,
                c.Radius,
                c.ArcAngleDegrees > 180.0,   // large arc flag
                SpiralType.Clothoid);

            editor.WriteMessage(
                $"\n  Built C{c.CurveNumber}: R={c.Radius:F2} m  " +
                $"A_in={c.AIn:F2}  A_out={c.AOut:F2}  Dir={c.Direction}");
        }
    }

    // ── Input helpers ──────────────────────────────────────────────────────

    private static IReadOnlyList<AlignmentReforge.Domain.Vertex>? PromptVertices(
        Editor editor, Database db)
    {
        var opts = new PromptEntityOptions("\nSelect source polyline: ");
        opts.SetRejectMessage("\nMust select a Polyline.");
        opts.AddAllowedClass(typeof(Polyline), false);

        var result = editor.GetEntity(opts);
        if (result.Status != PromptStatus.OK) return null;

        using var tr = db.TransactionManager.StartTransaction();
        if (tr.GetObject(result.ObjectId, OpenMode.ForRead) is not Polyline pl)
        {
            editor.WriteMessage("\nCould not read polyline.");
            return null;
        }

        var vertices = new List<AlignmentReforge.Domain.Vertex>(pl.NumberOfVertices);
        for (var i = 0; i < pl.NumberOfVertices; i++)
        {
            var pt = pl.GetPoint2dAt(i);
            vertices.Add(new AlignmentReforge.Domain.Vertex(
                i, new Point2D(pt.X, pt.Y), VertexHint.Unknown));
        }

        tr.Commit();
        return vertices;
    }

    private static AlignmentInputCase PromptInputCase(Editor editor)
    {
        var opts = new PromptKeywordOptions(
            "\nInput case [Auto/Case1/Case2] <Auto>: ", "Auto Case1 Case2")
        { AllowNone = true };

        var result = editor.GetKeywords(opts);
        if (result.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(result.StringResult))
            return AlignmentInputCase.Auto;

        return result.StringResult switch
        {
            "Case1" => AlignmentInputCase.Case1GeometryPoints,
            "Case2" => AlignmentInputCase.Case2CenterlineSamples,
            _       => AlignmentInputCase.Auto
        };
    }

    private static SolvedAlignment SolveByCase(
        AlignmentReconstructionEngine engine,
        IReadOnlyList<AlignmentReforge.Domain.Vertex> vertices,
        AlignmentInputCase inputCase)
    {
        return inputCase switch
        {
            AlignmentInputCase.Case1GeometryPoints   => engine.Reconstruct(vertices, autoClassifyUnknownHints: true),
            AlignmentInputCase.Case2CenterlineSamples => engine.ReconstructCase2(vertices),
            _                                         => engine.ReconstructAuto(vertices)
        };
    }

    // ── Utilities ──────────────────────────────────────────────────────────

    private static Point3d ToPoint3d(Point2D p) => new(p.X, p.Y, 0.0);

    private static string ResolveStyleName(StyleCollectionBase styles, string preferred, string fallback)
    {
        if (styles.Contains(preferred))  return preferred;
        if (styles.Contains(fallback))   return fallback;
        styles.Add(fallback);
        return fallback;
    }
}
#endif
