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
- **F1 Rules Engine** — `src/Rules`
- **F2 Project Setup** — level classification, preflight checklist, family loading, view range — `src/Setup`, `src/UI`

Logs: `%LOCALAPPDATA%\SleevesOpenings\logs`.
