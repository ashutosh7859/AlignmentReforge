# AlignmentReforge — Claude Code Reference

## What This Project Does
Reverse-engineers highway alignment geometry from Civil 3D polyline vertex data.
Recovers exact design parameters (R, Ls, A, stations) and rebuilds the alignment
in Civil 3D using the same parameters the original designer used.
No curve fitting. No optimization. Direct algebraic read from geometry.

## Project Structure
```
AlignmentReforge/
├── src/
│   ├── AlignmentReforge.Domain/        ← Models.cs — all records and enums
│   ├── AlignmentReforge.Geometry/      ← Core computation, no Autodesk refs
│   │   ├── GeometryMath.cs             ← Static math primitives
│   │   ├── Case1Solver.cs              ← Exact algebraic solver for Case 1
│   │   ├── Case2Solver.cs              ← Curvature trapezoid reader for Case 2
│   │   ├── Engine.cs                   ← Zone detection, build plan, validation
│   │   └── AlignmentSampler.cs         ← Synthetic Case 2 data generator
│   ├── AlignmentReforge.Console/       ← Test harness (Program.cs)
│   └── AlignmentReforge.Civil3D/       ← Civil3DCommands.cs — AutoCAD plugin
└── fixtures/
    ├── case1-sample.vertices.json      ← 258 vertices, 4 curves, ~9.1km
    └── case1-expected-results.json     ← Ground truth: R, Ls, A, stations
```

## Two Input Cases

### Case 1 — Exploded alignment polyline
Vertices ARE the geometry points (TS, SC, CS, ST). Data is exact.
- Gap vertices (large chord >= 80m) = arc chord connecting SC to CS
- Tangent vertices before TS and after ST give entry/exit bearings
- Ls_in  = ChainLength(TS to SC spiral vertices) — sum of all chord segments
- Ls_out = ChainLength(CS to ST spiral vertices)
- arc_chord = Distance(SC, CS)
- R solved from closure equation: f(R) = (Ls_in+Ls_out)/(2R) + 2*arcsin(chord/(2R)) - delta = 0
- Single root-find (Newton-Raphson + bisection bracket), converges in <8 steps
- All other params derived arithmetically — no iteration

### Case 2 — Interval-sampled centreline coordinates
Points lie exactly on geometry (Civil 3D export, zero noise).
Curvature profile is a perfect trapezoid:
- Tangent:    k = 0
- Spiral in:  k = s/(R*Ls)   linear ramp 0 to 1/R
- Arc:        k = 1/R         constant plateau
- Spiral out: k = (Ls-s)/(R*Ls) linear ramp 1/R to 0
- Tangent:    k = 0

Recovery:
- R = 1/k_plateau (median of plateau vertices)
- Plateau detected by finite difference: |k[i+1] - k[i-1]| / k_peak < 0.01
- TS, SC, CS, ST = interpolated from ramp line fits (closed form least squares)
- Ls_in = SC_station - TS_station
- Ls_out = ST_station - CS_station

## Key Mathematical Facts
- Bearing convention: surveying azimuth, degrees clockwise from north
  tx = sin(bearing), ty = cos(bearing)
- Offset vector for right turn: px = +ty, py = -tx
- Offset vector for left turn:  px = -ty, py = +tx
- Spiral offset formula (exact Fresnel series):
  offset = s^3/(6*R*Ls) * [1 - (s^2/(R*Ls))^2/56 + ...]
- OsculatingCurvature: exact 3-point formula, NO correction factor
  k = -2*cross(p1-p0, p2-p1) / (|p0p1|*|p1p2|*|p0p2|)

## Current Status

### Case 1: PASSING
```
dotnet run --project src/AlignmentReforge.Console/AlignmentReforge.Console.csproj -- verify
```
All 4 curves verified against fixture to sub-millimetre:
C1: R=1000m  Ls=100m
C2: R=1100m  Ls=100m
C3: R=700m   Ls=100m
C4: R=1000m  Ls=100m

### Case 2: BROKEN — fix needed first
```
dotnet run --project src/AlignmentReforge.Console/AlignmentReforge.Console.csproj -- selfcheck-case2
```
Current output: Curve count mismatch. Expected 4, actual 0.

Root cause: ComputeCurvatureProfile in Case2Solver.cs has wrong implementation.
The correct implementation is:

```csharp
internal static double[] ComputeCurvatureProfile(IReadOnlyList<Vertex> v)
{
    var n = v.Count;
    var k = new double[n];
    for (var i = 1; i < n - 1; i++)
        k[i] = OsculatingCurvature(v[i-1].Position, v[i].Position, v[i+1].Position);
    k[0]     = k[1];
    k[n - 1] = k[n - 2];
    return k;
}
```

DO NOT add any correction factor. The raw osculating curvature is exact for
noiseless Civil 3D data. After fixing, run selfcheck-case2. Expected: 4 curves,
all parameters within tolerances defined in VerifyCase2Equivalent in Program.cs.

## Validation Gates (before any Civil 3D API call)
Every curve must pass ALL of these or build is refused:
- arc_angle > 0
- R > 0, A_in > 0, A_out > 0
- StaTS < StaSC < StaCS < StaST
- No overlap between adjacent curves
- All tangent lengths >= 0.10m

## Civil 3D Build Sequence
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

## Naming Convention
Single alignment:  REFORGE_{yyyyMMdd_HHmmss}
Split part N:      REFORGE_{yyyyMMdd_HHmmss}_PN

## Split Alignment Logic
When some zones SOLVED, some INCONCLUSIVE:
- Group consecutive SOLVED zones into contiguous blocks
- Build one alignment per block
- INCONCLUSIVE zones never touch Civil 3D API
- User prompted to confirm split before any transaction opens
- Design speed NOT set by API — user sets in alignment properties after

## Important Notes
- Civil 3D MSB3277 warnings are harmless and permanent — ignore them
- All catch blocks must use System.Exception not Exception (Autodesk namespace conflict)
- Entity-based model (AddFixedLine + AddFreeSCS) NOT PI-based — critical for accuracy
- PI-based creation drifts from original design, entity-based is exact

## Commands Reference
```
dotnet build src/AlignmentReforge.Console/AlignmentReforge.Console.csproj
dotnet run --project src/AlignmentReforge.Console/AlignmentReforge.Console.csproj -- verify
dotnet run --project src/AlignmentReforge.Console/AlignmentReforge.Console.csproj -- selfcheck-case2
dotnet run --project src/AlignmentReforge.Console/AlignmentReforge.Console.csproj -- solve
dotnet run --project src/AlignmentReforge.Console/AlignmentReforge.Console.csproj -- solve --case2
dotnet run --project src/AlignmentReforge.Console/AlignmentReforge.Console.csproj -- selfcheck-case2 --interval 5
```

## Next Steps After Case 2 Passes
1. Implement split-alignment logic in Civil3DCommands.cs
2. Build DLL and load into Civil 3D 2026
3. Run CASE1ALIGNMENTDRYRUN on actual polyline
4. Confirm recovered parameters match original design
5. Run CASE1ALIGNMENTBUILD and verify generated alignment
