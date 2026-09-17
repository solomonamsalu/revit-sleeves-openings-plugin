# Sleeves & Openings — Revit add-in

Automates the office *Sleeves & Openings Standards Manual* inside Revit 2024.
See [FEATURES.md](FEATURES.md) for the full plan.

## Build & install
```
dotnet build -c Release
install.bat        # copies to %ProgramData%\Autodesk\Revit\Addins\2024
```
Restart Revit; a **Sleeves & Openings** ribbon tab appears.

## Rules
All sizes/clearances live in `Rules/rules.json` (inches). Lookup order:
1. `<model>.sleeves-rules.json` next to the RVT (project override)
2. `%APPDATA%\SleevesOpenings\rules.json` (office/user override — *Edit Rules* creates it)
3. `Rules/rules.json` next to the DLL (shipped default)

Edit `families` in rules.json to match your library's family names and parameter names;
drop the RFA files into `Families/` so *Project Setup* can load them.

## Implemented

| Feature | Ribbon | Where |
|---|---|---|
| **Workflow** — the manual's 13 steps with live status; one click launches the right button | Setup → Workflow | `src/Commands/WorkflowCommand.cs` |
| **General Rules gate** — Owner's plan / latest file / always-verify must be confirmed before any work (per user, per day) | automatic | `src/Setup/WorkGate.cs` |
| **F1 Rules Engine** — every size/clearance from the manual in `rules.json` | Setup → Reload / Edit Rules | `src/Rules` |
| **F2 Project Setup** — level roles, "always verify" checklist, family loading, view range | Setup → Project Setup | `src/Setup` |
| **Map Families / Create Test Families** — pick loaded families per role, or generate parametric test families | Setup | `src/Placement/FamilyMapping.cs`, `src/Setup/TestFamilyFactory.cs` |
| **F3 Smart Placement** — Exhaust, Chute, Dryer, Damper, Refrigeration, Storm, Area Drain ×2, Condensate, Standpipe, Bathtub; guards for columns / shear walls / beams | Place – Mechanical, Place – Plumbing / FP | `src/Placement`, `src/Commands/PlaceCommands.cs` |
| **F4 Riser Propagation** — copy openings floor by floor to each riser's stop level | Risers → Propagate Risers | `src/Risers/Propagator.cs` |
| **F9 Riser Manager** — every riser with floors, sizes, gaps, offsets; select / zoom | Risers → Riser Manager | `src/UI/RiserManagerForm.cs` |
| **F8 Final Check** — sizes, structure (exact walls), wall edges, mid-room, roof spacing, ERV 2', standpipe c-c and wall, overlaps, riser gaps/offsets, AD pairs, naming; one-click **Fix** for unambiguous issues; CSV export | Check → Final Check | `src/Audit/Auditor.cs` |
| **F5 Roof Generator** — top floor → roof with roof sizes (+4" exhaust/damper, 6"×6" dryer, +3" refrigeration, 2" ELECTRIC) and auto-spacing | Plan → Generate Roof | `src/Roof/RoofGenerator.cs` |
| **F6 Electrical Riser** — apartments + roof → conduit circles at 0.75" c-c, recalculated at offsets; opening on every floor, circles as detail lines, 2" roof sleeve | Plan → Electrical Riser | `src/Electrical/ElectricalPlanner.cs` |
| **F7 Refrigeration Plan** — PTAC / split / VRF, stacks, floors served, condenser roof, coverage check; Pipe Reference Openings on every floor (+3" on roof) | Plan → Refrigeration Plan | `src/UI/RefrigerationForm.cs`, `src/Commands/RefrigerationCommand.cs` |
| **F10 Documentation** — shared parameters `SO System` / `SO Riser` / `SO Size`, schedule per category, tag all openings in a plan, sync parameters | Document | `src/Placement/SharedParams.cs`, `src/Commands/DocumentationCommands.cs` |

Every element the add-in places carries a stamp (extensible storage) with system, riser id, size and level;
propagation, roof generation, the auditor and the schedule all read it.

Logs: `%LOCALAPPDATA%\SleevesOpenings\logs`.
