# AlignmentReforge

## Project Overview

AlignmentReforge reverse-engineers highway alignment geometry from Civil 3D polyline vertex data. Given a polyline exported from Civil 3D, it recovers the exact design parameters (radius R, spiral length Ls, A-value, stations TS/SC/CS/ST) and can rebuild the alignment in Civil 3D using entity-based construction (AddFixedLine + AddFreeSCS) that reproduces the original design exactly.

Two input modes:
- **Case 1** — vertices ARE the geometry points (TS, SC, CS, ST exported directly). The SC-to-CS arc is represented by a single long chord (gap vertex).
- **Case 2** — vertices are interval-sampled centreline coordinates (e.g. every 5m or 20m). The curvature trapezoid reader detects curves from the curvature profile.

## Architecture

```
AlignmentReforge/
├── CLAUDE.md                          ← this file
├── MONITORING.md                      ← logging/dashboard instructions
├── AlignmentReforge.sln               ← Visual Studio solution (4 projects)
├── fixtures/                          ← test data (JSON)
│   ├── case1-sample.vertices.json     ← 258 vertices, 4 curves, ~9.1km
│   ├── case1-expected-results.json    ← ground truth R, Ls, A, stations
│   ├── case2-sample.vertices.20m.json ← sampled at 20m interval
│   ├── case2-expected-results.20m.json
│   ├── case2-sample.vertices.5m.json  ← sampled at 5m interval
│   └── case2-expected-results.5m.json
└── src/
    ├── CLAUDE.md                      ← domain-specific reference (math, Civil 3D API, next steps)
    ├── AlignmentReforge.Domain/       ← pure data — records, enums, no logic
    │   └── Models.cs
    ├── AlignmentReforge.Geometry/     ← core computation — zero Autodesk references
    │   ├── GeometryMath.cs            ← static math primitives (distance, azimuth, curvature, spiral)
    │   ├── Case1Solver.cs             ← solves one zone from exact geometry points
    │   ├── Case2Solver.cs             ← solves all curves from sampled centreline data
    │   ├── Engine.cs                  ← orchestrator: classify → detect zones → solve → validate
    │   └── AlignmentSampler.cs        ← generates Case 2 input from a solved alignment
    ├── AlignmentReforge.Console/      ← CLI test harness
    │   ├── Program.cs                 ← top-level entry, command dispatch
    │   ├── FixtureIo.cs               ← JSON fixture loading/writing + DTOs
    │   └── Verifier.cs                ← test comparison logic
    └── AlignmentReforge.Civil3D/      ← AutoCAD plugin (conditionally compiled)
        └── Civil3DCommands.cs         ← ALIGNMENTDRYRUN + ALIGNMENTBUILD commands
```

### Dependency Graph

```
Domain  (leaf — no project references)
  ↑
Geometry  (references Domain only)
  ↑
Console   (references Domain + Geometry)
Civil3D   (references Domain + Geometry + Autodesk DLLs via #if CIVIL3D)
```

### Data Flow

```
Vertices (JSON or polyline)
  → Engine.Reconstruct / ReconstructCase2 / ReconstructAuto
    → ClassifyVertices (Case 1) or ComputeCurvatureProfile (Case 2)
    → DetectZones / DetectCurveGroups
    → Case1Solver.SolveZone / Case2Solver.SolveGroup  (per curve)
    → BuildPlan  (tangent/spiral/arc sequence)
    → BuildValidation  (Civil 3D safety gate)
  → SolvedAlignment
    → Civil3DCommands.BuildEntitiesFromSolution  (AddFixedLine + AddFreeSCS)
```

---

## Module Map

### Module: Domain
- **Purpose:** Pure data definitions — all records, enums, and value types
- **Files:** `src/AlignmentReforge.Domain/Models.cs`
- **Dependencies:** None (leaf module)
- **Used by:** Every other module
- **Entry point:** Read `Models.cs` top to bottom; types are grouped by concern with section comments
- **Tests:** None (data-only, no logic except `Point2D.DistanceTo`)

### Module: GeometryMath
- **Purpose:** Static math primitives for bearing, distance, curvature, spiral offset, linear regression
- **Files:** `src/AlignmentReforge.Geometry/GeometryMath.cs`
- **Dependencies:** Domain (`Point2D`)
- **Used by:** Case1Solver, Case2Solver, Engine, AlignmentSampler (via `using static`)
- **Entry point:** All methods are `internal static` — scan method signatures for the API surface
- **Tests:** Indirectly tested through `verify` and `selfcheck-case2` commands

### Module: Case1Solver
- **Purpose:** Solves a single transition zone from exact geometry points (TS/SC/CS/ST vertices)
- **Files:** `src/AlignmentReforge.Geometry/Case1Solver.cs`
- **Dependencies:** Domain, GeometryMath
- **Used by:** Engine (`AssembleCase1`)
- **Entry point:** `SolveZone()` — single public-facing method
- **Tests:** `dotnet run -- verify` against `fixtures/case1-expected-results.json`

### Module: Case2Solver
- **Purpose:** Solves all curves from interval-sampled centreline data using curvature trapezoid reader
- **Files:** `src/AlignmentReforge.Geometry/Case2Solver.cs`
- **Dependencies:** Domain, GeometryMath
- **Used by:** Engine (`ReconstructCase2`)
- **Entry point:** `SolveAll()` — computes curvature profile, detects groups, solves each
- **Tests:** `dotnet run -- selfcheck-case2` (round-trip: Case 1 → sample → Case 2 → compare)

### Module: Engine
- **Purpose:** Top-level orchestrator — vertex classification, zone detection, build plan assembly, validation
- **Files:** `src/AlignmentReforge.Geometry/Engine.cs`
- **Dependencies:** Domain, GeometryMath, Case1Solver, Case2Solver
- **Used by:** Console (Program.cs), Civil3D (Civil3DCommands.cs)
- **Entry point:** `AlignmentReconstructionEngine` class — public API is `Reconstruct()`, `ReconstructCase2()`, `ReconstructAuto()`
- **Tests:** All console commands exercise this module

### Module: AlignmentSampler
- **Purpose:** Generates Case 2 sampled vertices from a solved alignment (for self-check testing)
- **Files:** `src/AlignmentReforge.Geometry/AlignmentSampler.cs`
- **Dependencies:** Domain, GeometryMath
- **Used by:** Console (`selfcheck-case2` and `generate-case2-fixtures` commands)
- **Entry point:** `AlignmentSampler.SampleCase2()`
- **Tests:** Indirectly via `selfcheck-case2`

### Module: Console
- **Purpose:** CLI test harness — loads fixtures, runs solver, verifies results
- **Files:** `src/AlignmentReforge.Console/Program.cs`, `src/AlignmentReforge.Console/FixtureIo.cs`, `src/AlignmentReforge.Console/Verifier.cs`
- **Dependencies:** Domain, Geometry (Engine + AlignmentSampler)
- **Used by:** Developer (command line)
- **Entry point:** `Program.cs` top-level statements → `switch (command)`
- **Tests:** This IS the test runner

### Module: Civil3D
- **Purpose:** AutoCAD/Civil 3D plugin — ALIGNMENTDRYRUN and ALIGNMENTBUILD commands
- **Files:** `src/AlignmentReforge.Civil3D/Civil3DCommands.cs`
- **Dependencies:** Domain, Geometry (Engine), Autodesk DLLs (conditionally compiled via `#if CIVIL3D`)
- **Used by:** AutoCAD Civil 3D 2026 (NETLOAD command)
- **Entry point:** `AlignmentReforgeCommands.DryRun()` and `BuildAlignment()`
- **Tests:** No automated tests — requires Civil 3D runtime

---

## Coding Conventions

### Language & Framework
- C# 12, .NET 8, nullable reference types enabled, implicit usings enabled
- All projects target `net8.0` except Civil3D which targets `net8.0-windows`

### Naming
- **Namespaces** match folder structure: `AlignmentReforge.Domain`, `AlignmentReforge.Geometry`, etc.
- **Types** are PascalCase. Records are used heavily for immutable data.
- **Methods** are PascalCase. Private helpers use descriptive names without prefixes.
- **Fields** use `_camelCase` with underscore prefix (e.g. `_s` for settings).
- **Local variables** use `camelCase`. Single-letter names only for loop indices and well-known math symbols (`R`, `s`, `k`, `n`).
- **Constants** are PascalCase (`TwoPi`, `NearZeroDistance`).
- **Alignment abbreviations** are uppercase: `TS`, `SC`, `CS`, `ST`, `StaTS`, `StaSC`, etc.

### Style
- File-scoped namespaces (`namespace X;` not `namespace X { }`)
- `sealed record` for data types; `sealed class` for stateful types
- `internal static` for implementation classes (GeometryMath, Case1Solver, Case2Solver)
- `public sealed class` only for `AlignmentReconstructionEngine` and `AlignmentSampler` (the public API)
- `using static` for GeometryMath in files that use it heavily
- Section dividers use `// ── name ──────` or `// ═══════════` box comments for major sections
- No regions. No partial classes.

### Exception Handling
- **Always use `System.Exception`** in catch blocks, never bare `Exception` — Autodesk has a namespace-level `Exception` that shadows it
- Solver failures throw `InvalidOperationException` with descriptive messages
- Engine catches solver exceptions and wraps them in `InconclusiveZone` records

### Records Pattern
- All domain types are immutable records with positional syntax
- `with` expressions used for mutations (e.g. `vertex with { Hint = VertexHint.Curve }`)
- `IReadOnlyList<T>` for all collections in record parameters

---

## Development Rules

### Do
- Run `dotnet run -- verify` after ANY change to solver logic — this is the ground truth gate
- Run `dotnet run -- selfcheck-case2` after changes to Engine, Case2Solver, or AlignmentSampler
- Keep Geometry assembly free of Autodesk references — it must compile without Civil 3D installed
- Keep GeometryMath as a pure leaf module — no dependencies beyond `Point2D`
- Use `using static GeometryMath` in Geometry project files instead of duplicating math utilities
- Use `Point2D.DistanceTo()` for cross-assembly distance calculations (Console, Civil3D projects)
- Add new domain types to `Models.cs` — one file for all records and enums
- Use `SolverSettings` for any new tuning parameter — never hardcode thresholds in solver logic

### Don't
- Don't use PI-based alignment construction — entity-based (AddFixedLine + AddFreeSCS) is the only correct approach. PI-based creation drifts from original design parameters.
- Don't add correction factors to `OsculatingCurvature` — the exact 3-point formula is correct as-is
- Don't average multiple tangent segments for entry/exit bearing in Case 1 — single-segment is intentional to avoid curve-adjacent bias (see `AverageTangentBearing` in Engine.cs)
- Don't duplicate Distance/Deg2Rad/etc. — use GeometryMath or Point2D.DistanceTo
- Don't ignore MSB3277 warnings from the Civil3D project — they are harmless and permanent (Autodesk DLL version conflicts)
- Don't make GeometryMath, Case1Solver, or Case2Solver public — they are internal implementation details behind the Engine API

---

## Testing Strategy

### What's Tested
| Test | Command | What it verifies |
|------|---------|-----------------|
| Case 1 exact | `dotnet run -- verify` | All 4 curves match ground truth to sub-mm (R, Ls, A, stations, indices) |
| Case 2 round-trip | `dotnet run -- selfcheck-case2` | Sample at 20m → re-solve → parameters match within interval-dependent tolerance |
| Case 2 dense | `dotnet run -- selfcheck-case2 --interval 5` | Same at 5m — tighter tolerance, slower |
| Case detection | `dotnet run -- solve --case2` / `--case1` / `--auto-classify` | Manual case override |
| Build plan | `dotnet run -- dump-plan` | JSON output of tangent/spiral/arc sequence |

### All Console Commands
```bash
dotnet run --project src/AlignmentReforge.Console -- solve                    # default Case 1 solve
dotnet run --project src/AlignmentReforge.Console -- solve --case2            # force Case 2
dotnet run --project src/AlignmentReforge.Console -- solve --auto-classify    # auto-detect case
dotnet run --project src/AlignmentReforge.Console -- verify                   # Case 1 ground truth check
dotnet run --project src/AlignmentReforge.Console -- selfcheck-case2          # round-trip at 20m
dotnet run --project src/AlignmentReforge.Console -- selfcheck-case2 --interval 5  # round-trip at 5m
dotnet run --project src/AlignmentReforge.Console -- dump-plan                # JSON build plan
dotnet run --project src/AlignmentReforge.Console -- generate-case2-fixtures  # write Case 2 fixture files
dotnet run --project src/AlignmentReforge.Console -- generate-case2-fixtures --interval 5
```

### What's Missing
- No unit test project (xUnit/NUnit) — all testing is integration-level via the Console harness
- No isolated tests for GeometryMath functions (Distance, Azimuth, Deflection, SpiralOffset, OsculatingCurvature)
- No tests for edge cases: zero-length spirals (pure arc), single curve, reverse curves, S-curves
- No tests for the auto-classify heuristic (`DetectCase`)
- No Civil 3D end-to-end test (requires live Civil 3D 2026 runtime)
- No test for split-alignment logic (not yet implemented)

### Verification Exit Codes
- `0` — all checks passed
- `1` — missing fixture or invalid command
- `2` — verification failed (parameters out of tolerance)

---

## Modification Guidelines

### Adding a New Solver Case (e.g. Case 3)
1. Define any new domain types in `Models.cs`
2. Create `Case3Solver.cs` in the Geometry project (internal static class)
3. Add `ReconstructCase3()` to `AlignmentReconstructionEngine` in `Engine.cs`
4. Update `DetectCase()` heuristic if auto-detection should trigger it
5. Add a console command in `Program.cs`
6. Add fixture files under `fixtures/`
7. Run `verify` and `selfcheck-case2` to confirm no regression

### Adding a New Validation Check
1. Add the check inside `BuildValidation()` in `Engine.cs`
2. Use `ValidationSeverity.Error` for anything that would crash Civil 3D
3. Use `ValidationSeverity.Warning` for suspicious but buildable geometry
4. The validation gate in `Civil3DCommands.BuildAlignment()` automatically picks up new errors

### Adding a New SolverSettings Parameter
1. Add the parameter with a default value to the `SolverSettings` record in `Models.cs`
2. Reference it via `_s.NewParameter` in solver/engine code
3. Existing callers are unaffected (positional record with defaults)

### Adding Civil 3D Commands
1. Add methods to `AlignmentReforgeCommands` in `Civil3DCommands.cs`
2. Use `#if CIVIL3D` — the file must compile even without Autodesk DLLs
3. Always use `System.Exception` in catch blocks
4. Alignment naming convention: `REFORGE_{yyyyMMdd_HHmmss}` (split parts: `_PN` suffix)

### Modifying Math in GeometryMath
1. This is a leaf module used by everything — changes here propagate everywhere
2. Run BOTH `verify` and `selfcheck-case2` after any change
3. Never add external dependencies to this file
4. Keep methods pure and stateless

---

## Known Technical Debt

1. **No unit test project** — all validation runs through integration-level console commands. Individual functions (curvature, spiral offset, bearing) have no isolated tests. A test project would catch regressions faster and enable TDD for new solvers.

2. **AverageTangentBearing is a single-segment lookup disguised as a loop** — `Engine.cs` lines 263-292. The loop machinery (circular mean, sin/cos accumulation) exists but the bounds limit it to exactly one segment. Works correctly, but the code is more complex than necessary. Kept as-is because the machinery enables easy extension if multi-segment averaging is ever needed.

3. **Split-alignment logic not implemented** — `Civil3DCommands.cs` handles inconclusive zones by prompting the user, but does not yet group consecutive solved zones into separate alignments. The naming convention (`REFORGE_{date}_PN`) is defined but not wired up.

4. **Civil 3D end-to-end untested** — the plugin compiles and the entity-building logic is implemented, but it has not been tested in a live Civil 3D 2026 session. The `#if CIVIL3D` conditional means the plugin code only compiles when Autodesk DLLs are present.

5. **PlateauUniformityTolerance in SolverSettings is defined but unused** — `Models.cs` line 78. The Case 2 solver uses a different plateau detection method (finite-difference threshold) instead of this parameter.

6. **Case 1 chord stations vs arc-length stations** — Case 1 stores the SC→CS gap as a chord distance (StaCS = StaSC + chord), which is systematically shorter than the true arc length. This is handled correctly in `AlignmentSampler` and `Verifier` via arc-length correction chains, but the discrepancy adds complexity to any code that compares Case 1 and Case 2 station values.

7. **Fixture DTOs live in Console project** — `VertexFixtureDocument`, `ExpectedFixtureDocument`, etc. in `FixtureIo.cs` are tightly coupled to the JSON fixture format. If another project needs to read fixtures, these types would need to be shared.

---

## Key Mathematical Facts

Reference for anyone working on solver logic:

- **Bearing convention:** surveying azimuth, degrees clockwise from north, range [0, 360)
  - Unit tangent: `tx = sin(bearing)`, `ty = cos(bearing)`
- **Offset vector for right turn:** `px = +ty`, `py = -tx`
- **Offset vector for left turn:** `px = -ty`, `py = +tx`
- **Spiral offset** (exact Fresnel series): `offset = s^3/(6*R*Ls) * [1 - (s^2/(R*Ls))^2/56 + ...]`
- **Osculating curvature** (exact 3-point, NO correction factor): `k = -2*cross(p1-p0, p2-p1) / (|p0p1|*|p1p2|*|p0p2|)`
- **Spiral angle:** `theta_s = Ls / (2*R)` radians
- **A-value:** `A = sqrt(R * Ls)`
- **Arc angle:** `arc_angle = delta - theta_in - theta_out`

---

## Civil 3D Reference

### Build Sequence
```csharp
// 1. Create alignment
Alignment.Create(doc, name, null, "0", styleName, labelSetName)

// 2. Add tangents as fixed lines
alignment.Entities.AddFixedLine(start, end)

// 3. Add each curve as FreeSCS between bounding tangents
alignment.Entities.AddFreeSCS(
    prevTangentId, nextTangentId,
    AIn, AOut,
    SpiralParamType.AValue,
    Radius,
    arcAngleDegrees > 180,
    SpiralType.Clothoid)
```

### Commands
- `ALIGNMENTDRYRUN` — solve and print parameters, touch no geometry
- `ALIGNMENTBUILD` — solve, validate, build alignment if all checks pass

### Naming
- Single alignment: `REFORGE_{yyyyMMdd_HHmmss}`
- Split part N: `REFORGE_{yyyyMMdd_HHmmss}_PN`
