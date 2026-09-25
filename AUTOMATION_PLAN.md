# Sleeves & Openings — Automation Plan (DWG + PDF → Revit)

Status: plan agreed 2026-09-25. **Phase 1 built** (Automate → Auto Run: existing check, PDF + DWG selection,
sheet/floor detection, floor → level matching). Awaiting a test in Revit on the HV copy. Test project: **24 Skillman Street**, HV model (HVAC openings).

**Phase 2 built** (legend): tag definitions are read in the same PDF pass from the ABBREVIATIONS table (column
pairs found by geometry), the SYMBOLS list (tag inside the symbol, description on/just above it) and schedules
(TAG/DESIGNATION column + SERVICE/TYPE/DESCRIPTION + schedule title). Classification by `rules.json` "legend".
24 Skillman mechanical: 108 abbreviations, 4 symbols (FSD, MD, GD, SD), 25 schedule tags; the section 3 table is
reproduced exactly. Auto Run shows them on a "Tag meanings" tab. Plumbing PDF legend layout differs — adapt in the plumbing phase.

**Phase 3 built** (DWG risers): per floor, riser symbols (`Riser Center` dynamic blocks) are grouped when side
by side; tag bubbles (`DUCT RISER`, attributes RISERTAG + RISERCOUNT → TX1, ERV-SA) are tied to a riser by their
connector line on an `*RISER*` layer — the line's far end is the riser even when it is drawn without a symbol
(KX risers); two tags on one group split it; sizes come from multileaders whose arrow points at the riser
("14X10 DN 18X10 UP" → 14X10 on this floor, 18X10 on the floor above). Viewport regions now include the view
target (they were offset before). Block/attribute/layer names are in `rules.json` "dwgProfile". A tag with no
own definition borrows a schedule tag with the same prefix (ERV-SA → ERV-1) and says so.
24 Skillman: 107 riser positions on 10 floors — 85 get an opening (48 KX/TX, 20 ERV, 17 dryer), 22 have no tag
(reported), a few bubbles not connected (reported). 5th floor matches the engineer's plan. Auto Run shows them on a
"Risers (DWG)" tab.

Phase 1 notes: DWG floors are found from **sheet layouts** (title block + the plan viewport gives the floor's
model-space region), falling back to model-space titles, then the file name (per-floor xrefs). PDF floors are the
largest-font single-floor plan title on a page; the drawing index is ignored. Verified outside Revit on 24 Skillman:
mechanical PDF 10/10 floors + legend p1 + riser diagram p14 + schedules p15; DWGs mechanical 10/10, plumbing 9/9,
sprinkler 9/9 (all 1/4" = 1'-0", inches).

## 1. Goal

Place sleeves and openings **automatically** from the engineer's drawings. The user does not type sizes or riser
names and does not click locations. The user picks the drawing files, clicks **Auto Run**, and reviews only what
the report flags.

First scope: **HVAC openings in the HV model**, read from the **mechanical DWG and PDF**. Plumbing and sprinkler
come later and use the same pipeline (section 12).

## 2. What exists today

The add-in already applies the manual's rules: opening sizes, clearances, family mapping (MPI families), propagation
floor by floor, roof sizes and spacing, electrical and refrigeration planners, Final Check with fixes, schedule, and
Adopt for hand-placed elements (see `FEATURES.md`, `README.md`).

What is still manual:

| Manual step | Where |
|---|---|
| Clicking locations | `PickPoint` in `PlaceCommandBase`, Electrical, Refrigeration, Area Drain |
| Typing sizes and riser names | `Ask(...)` dialogs in `PlaceCommands.cs` |
| Picking what to propagate and where it stops | `PropagateCommand` + `PropagateForm` |
| Project answers (AC system, apartments, bathtub, condensate) | Setup / Refrigeration / Electrical forms |
| "Place anyway?", missing family, Final Check fixes | `PlaceCommandBase`, `AuditForm` |
| General rules gate | `WorkGate` |

The automation replaces those **inputs**. The rule code stays as it is.

## 3. Test-project findings (24 Skillman)

**Input files**
- Mechanical PDF: `Mechanical/24 Skillman St.MH 20260716 OWNER.pdf` (18 pages, vector, real text; do not use the "(1)" copy).
  - p1: ABBREVIATIONS table + SYMBOLS list + general notes
  - p3–p12: one floor plan per page (Cellar, 1st–7th, Roof, Bulkhead); the title gives the floor ("5TH FLOOR PLAN", sheet M-305.00)
  - p14: DUCT RISER DIAGRAM (M-500), which shows the floors each riser runs through
  - p15: schedules: FAN (EF-1 bathroom, EF-2 kitchen/HWH, GX-1 parking inline fan), ERV (ERV-1), GRILLE (ED-1/ED-2), heaters, split systems
- Mechanical DWG: `Mechanical/24 Skilmen.dwg` (AutoCAD 2018 / AC1032, 44,923 entities, 12,928 texts, 19 layouts).
  - Layers per system: `M-DUCT-EXHST`, `M-DUCT-SUPP`, `M- DRYER-EXHAUST`, `M-RISER`, `M-TEXT-N`, …
  - Blocks: `DUCT RISER` ×75, `Exhasut Fan 1`, `Exhaust Fan 2`, `Ceiling Diffuser`, …
  - Also includes a drawing index, legends and PDF underlays. All floors are in one file.
- Per-floor Xrefs: `Revit/Xref/Xref ME/*.dwg`. The drafter split them by floor at the office's shared 0,0. Each file is one packed block.
- Reference for checking the result: `Structural/24 Skillman Sleeves & Openings 7-20-26 COMMENTS.pdf`. Its legend lists
  "HVAC OPENINGS" with KX1, KX2, TX1–TX3, GX-1, MD-1…MD-5, ERV, ERV-MD, ERV-FSD.

**Tag meanings on this project** (read from the PDF, not guessed):

| Tag | Meaning (source) | Opening? |
|---|---|---|
| KX / TX | Kitchen / toilet exhaust (**office-fixed**) | Yes, exhaust riser |
| GX-1 | Inline fan, parking area exhaust (fan schedule) | Yes, its duct riser |
| ERV-1 | Energy recovery ventilator (ERV schedule) | Yes |
| MD | Class I motorized damper (symbols) | Yes (manual rules 83–87) |
| FSD, GD | Fire smoke damper, gravity damper (symbols) | No own opening (assumption, 7.4) |
| EF-1 / EF-2 | Exhaust fans (fan schedule) | No |
| CD | **Ceiling diffuser** (abbreviations), not condensate | No |
| ED-1 / ED-2 | Exhaust/return grille (grille schedule) | No |
| AD | **Access door** (abbreviations); in plumbing AD = area drain | No |
| SD | Smoke damper or detector | No |
| GX-2, GX-3, EX, SA, NSP | Not defined in the PDF | Report "undefined tag" |

**Lessons**
- The same abbreviation means different things in different disciplines and projects, so definitions are read per PDF.
- Most tags are not risers. The definition decides whether an opening is needed.
- Parsing the DWG is solved. **Matching tags to risers is the hard part**: a naive "nearest text" matched only 5 of 75 `DUCT RISER` blocks.

## 4. Agreed rules

1. Inputs: **mechanical DWG and PDF, both used**.
2. Flow: **check the model → pick PDF + DWG → read abbreviations → extract locations → place → report**.
3. Tag meanings come from the engineer PDF (abbreviations → symbols → schedules). **Only KX and TX are office-fixed.**
4. A riser **with no tag in both the PDF and the DWG** is reported as **"tag missing"** and not placed. A tag in either file is enough.
5. **MD** gets its own opening: damper + 2" each side, same rules as roof exhausts (manual 83–87).
6. **FSD / GD**: no own opening. The ERV opening that has one is labelled `ERV-FSD` / `ERV-MD`. *Assumption, to be confirmed.*
7. Fans, diffusers, grilles, detectors and access doors get no opening.
8. Undefined tags are **reported, never guessed**.
9. The result is checked against the S&O COMMENTS PDF (HVAC OPENINGS). PL and HV are different models and are not compared with each other.
10. Work on a **copy** of the HV model during testing.

## 5. User flow

```
[Auto Run]
   1. Check model ─── existing openings? ── none ──────────────┐
                              │ some                           │
                              └─ Keep & add missing / Update / Cancel
   2. Select inputs ── PDF + DWG (remembered per project) ─────┤
   3. Read legend  ── abbreviations + symbols + schedules ─────┤
   4. Extract      ── DWG + PDF → merged riser list ───────────┤
   5. Place        ── openings in HV (no dialogs) ─────────────┤
   6. Report       ── placed / skipped / needs review ─────────┘
```

### Step 1 – Check the model for existing sleeves/openings
- Scan with the existing `Adopter.Scan(includeStamped: true)` + `RiserIndex.AllOpenings`, plus native shaft openings.
- Show a summary per system and floor. If any exist, the user picks:
  - **Keep & add missing**: place only riser/floor pairs that have no opening at that spot.
  - **Update**: also fix sizes that changed and report moved ones. Never delete without listing it.
  - **Cancel**.
- Existing = same system, same level, center within `riserTolerance` (6").

### Step 2 – Select inputs
- A window with one row per discipline (Mechanical first): **PDF** and **DWG** file pickers.
- Paths are saved in the project state (extensible storage) and filled in on the next run.
- An immediate check shows: pages/sheets found, floor plans matched to levels, legend found, riser diagram found.
  Any drawing floor that doesn't match a level can be matched from a dropdown (the only interactive choice, and only when needed).

### Step 3 – Read the legend (abbreviations)
- Sources, in order: **ABBREVIATIONS table** → **SYMBOLS list** → **schedules** (rows with TAG + SERVICE / DESCRIPTION) → office list (KX, TX only).
- Each entry becomes `tag → definition text → category`. The category comes from keyword rules in rules.json (7.1):
  `opening` (exhaust riser, ERV, motorized damper, chute, dryer exhaust, …) or `ignore` (diffuser, grille, fan, detector, access door, …).
- The legend is saved with the run and shown in the report.

### Step 4 – Extract and merge
See section 6.

### Step 5 – Place
- Build an `OpeningSpec` per riser per floor with the existing rules (duct + 2" each side, roof +4", MD + 2", ERV 2' roof spacing …).
- Place with the existing `Placer` and MPI families, one transaction group per run (one undo).
- Floors between the start and end of each riser: place directly per floor from the riser list, or reuse `Propagator`.
- Roof: existing `RoofGenerator` sizing rules.
- Guards: soft warnings are recorded, hard conflicts (column / shear wall / beam) are **skipped and reported**. No dialogs.
- Afterwards: Final Check runs, and unambiguous fixes are applied automatically.

### Step 6 – Report
See section 9.

## 6. Extraction pipeline

### 6.1 DWG (ACadSharp)
1. **Sheet index**: find plan titles (`MECHANICAL 5TH FLOOR PLAN`, `M-305.00`) in model space and layouts; ignore the
   drawing index table (same titles in a list). Each plan's area is the frame or viewport around its title.
2. **Floor → level**: title text → the existing `LevelClassifier` patterns + ordinal parsing (CELLAR, 1ST…7TH, ROOF, BULKHEAD).
3. **Riser candidates** per floor:
   - `DUCT RISER` blocks (name from the engineer profile), rectangles with diagonals on duct layers, circles on riser layers.
   - Text `UP`, `DN`, `UP & DN`, `DRYER EXHAUST UP`, `VENT UP IN SHAFT`.
4. **Tags**: TEXT, MTEXT, block attributes, **dynamic block properties** (check first: the `DUCT RISER` block may hold its size), and MultiLeader text.
5. **System** from layer (engineer profile map, e.g. `M-DUCT-EXHST` → exhaust).
6. Units: read `$INSUNITS`, apply block scale and rotation; **verify against one known riser** before trusting the result.

### 6.2 PDF (PdfPig)
1. Page → discipline + floor from the title block (`MECHANICAL`, `5TH FLOOR PLAN`, sheet number).
2. Words with positions; line/rectangle paths for riser symbols (rectangle with an X).
3. Legend pages (p1) and schedules (p15) → section 5 step 3.
4. Riser diagram (p14): column per riser, floor labels (`1ST FL` … `ROOF`), tag per column → floors served.

### 6.3 Tag → riser matching
- Candidates for each tag: riser symbols on the same floor.
- Score = leader/MultiLeader points at the symbol (strongest) > text inside or touching the symbol > nearest on the same system layer.
- **Max distance** per profile (start: 36" model units). Beyond that, no match.
- **Tie-break**: if the second-best candidate is within 25% of the best distance → low confidence, flagged.
- One tag can serve one riser; one riser can have one name tag + one size tag.

### 6.4 Size text
- `12X8` → duct W×H (inches). `12X8 DN 16X6 UP` → **12×8 on this floor and below, 16×6 on the floor above**.
- `4"`, `4"Ø`, `5%%C` (AutoCAD Ø) → round diameter.
- Size inside the block, if present, takes priority over nearby text; if the drawn rectangle differs from the text by more than 1", flag it.

### 6.5 PDF ↔ DWG alignment
- Per floor: tags that appear **once** on that floor in both files (e.g. `KX1`, `GX-1`) are anchor pairs.
- At least 2 anchors → similarity transform (scale + rotation + shift) from PDF page to DWG; residuals > 3" → flag the floor.

### 6.6 Merge DWG + PDF

| Case | Action |
|---|---|
| Both agree (location, size, name) | Place, **high** confidence |
| Same location, different size | Use the **PDF** size (issued set), report |
| Only in PDF | Place from aligned PDF position, report "not in DWG" |
| Only in DWG | **Do not place**, report "not in PDF (removed?)" |
| Riser symbol, no tag in either | **Do not place**, report "tag missing" |
| Tag defined as `ignore` (CD, ED, EF, SD, AD…) | Skip silently (count in report) |
| Tag not defined anywhere (GX-2, EX…) | Do not place, report "undefined tag" |

### 6.7 Riser assembly
- Stack same-name / same-system items within `riserTolerance` across consecutive floors into one riser.
- Start/end floor from `UP` / `DN` / `UP & DN` on each floor, checked against the riser diagram. On conflict, the plan wins (`floorPlanWinsOverRiserDiagram`).
- Output: **`risers.json`** saved next to the model (or in the run folder):

```json
{
  "project": "24 Skillman", "run": "2026-09-25T10:00",
  "inputs": { "pdf": "…MH 20260716 OWNER.pdf", "dwg": "…24 Skilmen.dwg" },
  "legend": [ { "tag": "MD", "definition": "CLASS I MOTORIZED DAMPER", "source": "pdf:symbols:p1", "category": "opening", "system": "MotorizedDamper" } ],
  "risers": [
    { "id": "KX1", "system": "Exhaust", "kind": "kitchen", "confidence": "high",
      "floors": [ { "level": "5TH FLOOR", "x": 12.4, "y": -3.1, "width": 12, "length": 8, "source": "both" } ],
      "from": "CELLAR", "to": "ROOF", "notes": [] }
  ],
  "issues": [ { "type": "tag missing", "level": "5TH FLOOR", "x": 1.0, "y": 2.0, "detail": "DUCT RISER block, no tag within 36\"" } ]
}
```

## 7. rules.json additions

### 7.1 Legend classification
```json
"legend": {
  "officeFixed": { "KX": "KITCHEN EXHAUST", "TX": "TOILET EXHAUST" },
  "categories": [
    { "match": "MOTORIZED DAMPER|MOTOR OPERATED DAMPER", "system": "MotorizedDamper" },
    { "match": "ENERGY RECOVERY", "system": "ERV" },
    { "match": "KITCHEN EXHAUST|TOILET EXHAUST|PARKING|GARAGE EXHAUST|EXHAUST RISER", "system": "Exhaust" },
    { "match": "DRYER", "system": "DryerExhaust" },
    { "match": "CHUTE", "system": "GarbageChute" },
    { "match": "DIFFUSER|GRILLE|REGISTER|FAN\\b|DETECTOR|ACCESS DOOR|HEATER", "ignore": true },
    { "match": "FIRE SMOKE DAMPER|GRAVITY", "ignore": true, "labelOnHost": true }
  ]
}
```

### 7.2 Engineer profile (one per engineering firm)
```json
"engineerProfiles": {
  "Precision Engineering Group": {
    "detect": "PRECISION ENGINEERING GROUP",
    "riserBlocks": ["DUCT RISER"],
    "layers": { "M-DUCT-EXHST": "Exhaust", "M- DRYER-EXHAUST": "DryerExhaust", "M-RISER": null },
    "maxTagDistanceIn": 36,
    "upDownWords": { "up": "UP", "down": "DN", "both": "UP & DN" }
  }
}
```
A new engineer means a new profile, not new code. The add-in can list a DWG's layers/blocks to help write one.

### 7.3 Automation behaviour
```json
"automation": {
  "existing": "keepAndAddMissing",
  "hardConflict": "skipAndReport",
  "softWarning": "placeAndReport",
  "autoFix": true,
  "gate": "confirmOncePerRun"
}
```

### 7.4 Open assumption
FSD/GD: no own opening; labelled on the ERV opening (`ERV-FSD`, `ERV-MD`). Revisit when confirmed.

## 8. Code layout (new)

```
src/Automation/
  AutoRunCommand.cs        ribbon button, orchestrates steps 1-6
  ExistingCheck.cs         step 1 (wraps Adopter + RiserIndex)
  InputSet.cs              step 2 model + persistence (ProjectStore)
  UI/AutoRunForm.cs        file pickers, floor matching, existing-openings choice
  Legend/LegendReader.cs   abbreviations, symbols, schedules → Legend
  Dwg/DwgSource.cs         ACadSharp: sheets, floors, symbols, texts, blocks (incl. nested)
  Pdf/PdfSource.cs         PdfPig: pages, words, paths, riser diagram
  Extract/TagMatcher.cs    6.3 matching + confidence
  Extract/SizeParser.cs    6.4 size grammar
  Extract/Alignment.cs     6.5 PDF↔DWG, DWG→Revit (link / grids / 0,0)
  Extract/RiserBuilder.cs  6.6 merge + 6.7 assembly → RiserList
  RiserList.cs             risers.json model (read/write)
  AutoPlacer.cs            step 5, uses OpeningSpec/Placer/Propagator/RoofGenerator/Auditor
  Report/AutoReport.cs     step 6 (HTML/CSV + zoom list)
```
- NuGet: **ACadSharp** (3.8.0, net48 + net10, verified reading this project's DWGs); **PdfPig** (verify net48 support before adding).
- Existing classes reused unchanged where possible. Dialog-free paths are added next to the interactive ones (e.g. a
  `PlaceCommandBase` core that takes points instead of clicks).

## 9. Report

- Header: inputs (file names + dates), engineer profile, legend used, level matching.
- Summary per floor × system: placed / already existed / updated / skipped.
- **Needs review** list (click → zoom in Revit): tag missing, undefined tag, DWG/PDF disagreement, low-confidence
  match, hard conflict skipped, floor not aligned, size text vs drawn size mismatch.
- Final Check result after auto-fix.
- Saved as HTML + CSV in the run folder together with `risers.json`.

## 10. Coordinates into Revit

Tried in order and reported which one was used:
1. The matching per-floor Xref is linked in the model → use its transform.
2. Grids: grid bubbles/lines in the DWG floor ↔ Revit grids by name → similarity transform.
3. Shared 0,0 (office QA standard: "CAD files have same (0,0) point").

**Safety check before placing**: 3 anchor risers must land within 1" of the expected Revit position (or of each
other across floors). If not, stop and report. Nothing is placed.

## 11. Phases

| # | Phase | Deliverable | Done when | Est. |
|---|---|---|---|---|
| 1 | Existing check + input window | `AutoRunCommand` steps 1–2, paths remembered | Runs on HV copy, lists existing openings, validates files | 2 d |
| 2 | Legend reader | Legend from PDF p1 + p15, classification | Table in section 3 reproduced for 24 Skillman | 2 d |
| 3 | DWG source | Sheets, floors, riser symbols, texts (nested blocks, units) | 5th floor: all `DUCT RISER` blocks + tags listed with positions | 3 d |
| 4 | PDF source + alignment | Pages, words, symbols, riser diagram; PDF↔DWG transform | 5th floor anchors align within 3" | 3 d |
| 5 | Match + merge + assemble | `risers.json` | 5th floor list matches S&O set HVAC openings by eye | 3 d |
| 6 | Auto placement | Openings in HV copy, no dialogs, rerun-safe | 5th floor placed, alignment check passes | 3 d |
| 7 | Report | HTML/CSV + review list | Every skipped/flagged item has a reason | 2 d |
| 8 | Whole building + tuning | All floors, roof, bulkhead | Agreed accuracy vs S&O COMMENTS PDF | 3–5 d |

**Total ≈ 4 weeks** for HV on 24 Skillman.

## 12. After HV works

1. Plumbing (PL model): sanitary, vent, storm, cold/hot water, gas sleeves. Sizes mostly from the riser diagram;
   add the missing systems to the add-in (the MPI sleeve family already has these sizes).
2. Sprinkler/standpipe.
3. Electrical (apartment count from the architectural DWG) and refrigeration (AC system from the mechanical schedules).
4. A second project with the same engineer, then one with a different engineer (new profile).

## 13. Risks

- Tags not linked to anything, or risers drawn as plain lines → lower confidence, flagged, not guessed.
- The DWG is older than the issued PDF → PDF wins on size; file dates shown in the report.
- Plumbing sizes only on schematic riser diagrams (phase 12).
- Alignment wrong → every opening is misplaced; hence the safety check in section 10.
- Judgment items in the manual (roof fire paths, "confirm with the lead") stay as review items.

## 14. Open questions

1. FSD / GD: confirm "no own opening, labelled on the ERV opening" (assumed).
2. Which `24 Skillman PL.rvt` copy is final (Sep 18 or Sep 23). Needed only for the plumbing phase.
3. Does the HV model already contain any openings? (Step 1 will show this.)
4. Insulation allowance on duct openings (QA sheet: "sizing is + insulation"): the thickness per system.
