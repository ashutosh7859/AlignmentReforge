# AlignmentReforge — Claude Code Reference

## What This Project Does
Reverse-engineers highway alignment geometry from Civil 3D polyline vertex data.
Recovers exact design parameters (R, Ls, A, stations) and rebuilds the alignment
in Civil 3D using the same parameters the original designer used.

## Project Structure
```
AlignmentReforge/
├── src/
│   ├── AlignmentReforge.Domain/        ← Models.cs — all records and enums
│   ├── AlignmentReforge.Geometry/      ← Core computation, no Autodesk refs
│   │   ├── GeometryMath.cs
│   │   ├── Case1Solver.cs
│   │   ├── Case2Solver.cs
│   │   ├── Engine.cs
│   │   └── AlignmentSampler.cs
│   ├── AlignmentReforge.Console/       ← Test harness (Program.cs)
│   └── AlignmentReforge.Civil3D/       ← Civil3DCommands.cs — AutoCAD plugin
└── fixtures/
    ├── case1-sample.vertices.json      ← 258 vertices, 4 curves, ~9.1km
    └── case1-expected-results.json     ← Ground truth: R, Ls, A, stations
```

## Current Status

### What's Working
- **Case 1 solver**: PASSING
  - All 4 curves verified to sub-millimetre
  - C1: R=1000m Ls=100m
  - C2: R=1100m Ls=100m
  - C3: R=700m Ls=100m
  - C4: R=1000m Ls=100m

- **Case 2 solver**: PASSING
  - Self-check passes at 5m and 20m intervals
  - All 4 curves recovered from sampled centreline data

- **Validation gates**: PASSING
  - All checks implemented and active

- **Civil 3D entity-based creation**: IMPLEMENTED
  - Uses AddFixedLine + AddFreeSCS (not PI-based)
  - Exact reconstruction, not approximate

### Blocked (Waiting)
- Split-alignment logic
- Civil 3D end-to-end test

## Key Mathematical Facts
- Bearing convention: surveying azimuth, degrees clockwise from north
  - tx = sin(bearing), ty = cos(bearing)
- Offset vector for right turn: px = +ty, py = -tx
- Offset vector for left turn: px = -ty, py = +tx
- Spiral offset formula (exact Fresnel series):
  - offset = s^3/(6*R*Ls) * [1 - (s^2/(R*Ls))^2/56 + ...]
- OsculatingCurvature: exact 3-point formula, NO correction factor
  - k = -2*cross(p1-p0, p2-p1) / (|p0p1|*|p1p2|*|p0p2|)

## Test Commands
```bash
dotnet run --project src/AlignmentReforge.Console -- verify
dotnet run --project src/AlignmentReforge.Console -- selfcheck-case2
dotnet run --project src/AlignmentReforge.Console -- solve
dotnet run --project src/AlignmentReforge.Console -- solve --case2
```

## Validation Gates (Must Pass ALL)
Every curve must pass or build is refused:
- arc_angle > 0
- R > 0, A_in > 0, A_out > 0
- StaTS < StaSC < StaCS < StaST
- No overlap between adjacent curves
- All tangent lengths >= 0.10m

## Civil 3D Build Sequence (Reference)
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
- Single alignment: REFORGE_{yyyyMMdd_HHmmss}
- Split part N: REFORGE_{yyyyMMdd_HHmmss}_PN

---

## **MONITORING & LOGGING** (DEFAULT FOR ALL SESSIONS)

### This is automatic. Claude Code reads this.

**Every task you work on should be logged.**

See MONITORING.md for detailed instructions.

### Quick Version:

When you START working on a task:
```bash
python C:\Users\tpi068\.claude-monitor-system\log.py task update "[Task Name]" "in_progress"
```

When you MODIFY files:
```bash
python C:\Users\tpi068\.claude-monitor-system\log.py file modified "[filepath]" "[what changed]"
```

When you RUN tests:
```bash
[Run your test command]
python C:\Users\tpi068\.claude-monitor-system\log.py command "[test command]" "[exit code]" "[result]"
```

When you FINISH a task:
```bash
python C:\Users\tpi068\.claude-monitor-system\log.py task update "[Task Name]" "done" "[summary]"
```

### Why this matters:
- You can see progress in real-time (dashboard)
- You catch problems early (stuck detection)
- You have an audit trail (what was done)
- No surprises (you know exactly what happened)

### Dashboard:
```bash
python C:\Users\tpi068\.claude-monitor-system\dashboard.py --project AlignmentReforge
```

---

## Next Steps (Priority Order)
1. **Split-alignment logic: implement in Civil3DCommands.cs**
   - Group consecutive SOLVED zones into blocks
   - Build one alignment per block

2. **Civil 3D test: load DLL and end-to-end verify**
   - Load into Civil 3D 2026
   - Test on actual polyline
   - Verify parameters match original design

---

## Important Notes
- Civil 3D MSB3277 warnings are harmless and permanent — ignore them
- All catch blocks must use System.Exception not Exception (Autodesk namespace conflict)
- Entity-based model (AddFixedLine + AddFreeSCS) NOT PI-based — critical for accuracy
- PI-based creation drifts from original design, entity-based is exact
- **Always log your work** — monitoring is on by default for all sessions