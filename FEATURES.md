# Sleeves & Openings Add-in — Feature Plan

Source: `Sleeves_and_Openings_Standards_Manual (1).pdf` (office standards manual).
Target: Revit 2024, C# / .NET Framework 4.8 (same stack as `RevitLocalService`).

## Guiding principle
The manual contains **hard rules** (sizes, clearances, spacing, naming) and **judgment**
(reading the engineer's PDF, choosing dead space). The add-in owns the hard rules completely
and assists the judgment: the drafter clicks where a riser is, the add-in does the rest.

---

## F1 — Rules Engine (foundation)
Single JSON rule set (`Rules/rules.json`), editable without recompiling.
- Per system: family, size formula (`pipe + 2"`, `duct + 2" each side`, fixed 28.5", 3", 10"),
  name/label (`ELECTRIC`, `DE`), roof adjustments (+4" exhaust, +3" W/L refrigeration, 6"x6" dryer).
- Clearance/spacing table: 1' from columns, 1' from walls/curbs on roof, 2' between roof openings,
  2' between ERVs, 8" between dryer vents, 2'-0" AD c-c, 5.5" standpipe-to-wall, 8" standpipe c-c,
  0.75" conduit c-c.
- Project overrides (naming convention, bathtub choice: two 6" vs one 10").
- Lookup order: `<project>.sleeves-rules.json` next to the RVT → `%APPDATA%\SleevesOpenings\rules.json`
  → add-in folder default.

## F2 — Project Setup / Preflight
- Load required families if missing.
- Detect and classify levels: cellar/lowest, apartment floors, highest apartment floor, setbacks,
  main roof, bulkhead. Persisted in the model (extensible storage).
- Set view range per manual (Top = Level Above, offset 0 / Bottom = Associated Level) on plan views.
- "Always verify" checklist: Owner's plan, latest file, shower drain type, wall-hung toilets,
  medicine cabinets, niches, condensate required?, bathtub sleeve type. Answers persisted.

## F3 — Smart Placement Tool (one command per system)
| Button | Input | Result |
|---|---|---|
| Exhaust Opening | click + duct WxH | Regular Opening at W+4", H+4" |
| Garbage Chute | click | 28.5" x 28.5", locked |
| Dryer Exhaust | click | 4" circle named DE inside a mech opening box |
| Motorized Damper | click + size | +2" each side |
| Refrigeration | click + line count | Pipe Reference Opening, Down Height = 0 |
| Electrical | click + apt count | Blue box, N+1 conduit circles at 0.75" c-c, labeled ELECTRIC |
| Storm | click + pipe size | pipe + 2"; Area Drain mode places **two** 10" at 2'-0" c-c |
| Condensate | click | 3" sleeve |
| Standpipe | click + pipe size | pipe + 2" |
| Toilet / Bathtub | click + type | per selected setup |

Placement-time guards: wall edge, shear wall, beam, < 1' from column, mid-room.

## F4 — Riser Propagation (copy down / copy up)
- Propagate selected openings from the highest apartment floor to a termination level, per riser.
- Termination level stored per riser (shared parameter).
- Offset handling: apply to floors below? flag offset.
- Same-size enforcement warning.

## F5 — Roof Generator
- Copy top-floor openings to roof with roof rules: exhaust +4", dryer 6"x6", refrigeration +3",
  garbage chute unchanged, electrical -> single 2" ELECTRIC sleeve.
- Layout helper: ERVs at exactly 2', dryer rows >= 8", >= 1' from walls/curbs, >= 2' apart,
  as close to riser as possible. Fire-path regions flagged. Same for setbacks.

## F6 — Electrical Conduit Calculator
- Circles per floor = remaining apartments + 1 roof; recalculated at each offset.

## F7 — Refrigeration Line Planner
- Wizard: PTAC? -> VRF/VRV vs split -> condenser locations -> apartments per stack.
- Line counts, skip penetrations where lines come from below, roof openings per condenser group,
  coverage check.

## F8 — Validation / "Final Check" Auditor
- Sizes, clearances, spacing, structural conflicts, riser continuity, completeness
  (two AD sleeves, condensate coverage, standpipes in both stairs, chute straight), naming.
- Dockable panel with click-to-zoom; CSV export.

## F9 — Riser Manager Panel
- Dockable list of every riser (system, size, start/end level, floors, offsets); select -> highlight.

## F10 — Schedules & Documentation
- Auto schedule (system, size, level, name, riser ID); tags.

---

## Build order
1. F1 + F2
2. F3 fixed-size systems (chute, condensate, dryer, storm/AD, standpipe)
3. F4 + F9
4. F8
5. F5
6. F6 + F7
7. F10

## Open questions
- Actual family names/parameters for Regular Opening, Pipe Reference Opening, sleeve families.
- Revit 2024 only, or multi-version?
- Toilet page (17) is images only: sizes/spacing for regular vs double toilet?
