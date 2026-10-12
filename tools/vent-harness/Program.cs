using System;
using System.Collections.Generic;
using System.Linq;
using SleevesOpenings.Automation;
using F = SleevesOpenings.Automation.StackFinder.Fixture;

// The Standards Manual's toilet sleeves page (p17) as coordinates (inches, then feet): VentFinder's vents along each row's wall.
static class Program
{
    static F Fx(string code, double xIn, double yIn, double bx, double by, string name = null) =>
        new F { Code = code, Name = name ?? code, X = xIn / 12, Y = yIn / 12, Back = bx == 0 && by == 0 ? null : new[] { bx, by },
                Points = new List<(double X, double Y)> { (xIn / 12, yIn / 12) } };

    static void Show(string title, List<F> fx, string expect, double ax, double ay, double ox = 0, double oy = 0, List<VentFinder.Existing> existing = null, VentFinder.Namer namer = null, string floor = "2")
    {
        var rules = new PlumbingRules();
        var r = VentFinder.Find(fx, existing ?? new List<VentFinder.Existing>(), rules, namer ?? new VentFinder.Namer(), floor);
        Console.WriteLine($"== {title}\n   manual: {expect}");
        foreach (var row in r.Rows)
        {
            Console.WriteLine($"   {row.Id} {row.Kind}: serves {string.Join(" ", row.Serves.Select(f => f.Code))}");
            foreach (var v in row.Vents.OrderBy(v => (v.X - ox / 12) * ax + (v.Y - oy / 12) * ay))
                Console.WriteLine($"      {v.Name,-9} {v.Kind,-16} {v.Size:0}\"  along {((v.X - ox / 12) * ax + (v.Y - oy / 12) * ay) * 12,7:0.##}\"  (x {v.X * 12:0.#}, y {v.Y * 12:0.#})  {v.How}" +
                                  (v.Notes.Count > 0 ? "  [" + string.Join("; ", v.Notes) + "]" : ""));
            foreach (var n in row.Notes) Console.WriteLine("      note: " + n);
            foreach (var n in row.Skipped) Console.WriteLine("      skipped: " + n);
        }
    }

    static void Main()
    {
        // regular toilet: wall along X at y = 0, the bathroom above it (back = down); toilet CL x = 0, LAV drain in the wall at 37"
        Show("Regular toilet", new List<F> { Fx("WC", 0, 15.5, 0, -1), Fx("LAV", 37, 0, 0, -1) },
             "G6 -4.25 | Y4 +1 | Y4 +8 | G4 +37 | Y6 +51.75", 1, 0);

        // double toilet: two bathrooms back to back on one wall (y = 0); upper faces down, lower faces up; both LAVs on one G4 (31")
        Show("Double toilet (back to back)", new List<F> { Fx("WC", 0, 15.5, 0, -1), Fx("LAV", 31, 0, 0, -1), Fx("WC", 62, -15.5, 0, 1), Fx("LAV", 32, 0, 0, 1) },
             "G6 8.9 | Y4 14.2 | Y4 21 | G4 31 | Y4 40.6 | Y6 50.8 | Y4 61.2 | G6 66.6 (one shared Y6)", 1, 0);

        // wall-hung: waste on the CL in the wall; vanity LAV drain at 33"
        Show("Wall-hung toilet", new List<F> { Fx("WC", 0, 0, 0, -1, "wall-hung toilet"), Fx("LAV", 33, 0, 0, -1) },
             "G6res -13 | G6 0 | Y4res +16 | Y4 +23 | G4 +33 | Y6 +43.25", 1, 0);

        // different setup: wall along Y at x = 0, room to the right (back = -X); LAV at y -22.8, WC CL y -52, tub drain (20, -81.8)
        Show("Different setup (side wall, tub)", new List<F> { Fx("LAV", 0, -22.8, -1, 0), Fx("WC", 15.8, -52, -1, 0), Fx("BT", 20, -81.8, 0, 0) },
             "from the top: Y6 9.8 | G4 22.8 | G6 48.8 | Y4 55 | Y4 tub 72 (y downward)", 0, -1);

        // two floors: the regular bathroom on floor 3 then floor 2 -> one stack name, lined up
        var namer = new VentFinder.Namer();
        Show("Regular, floor 3", new List<F> { Fx("WC", 0, 15.5, 0, -1), Fx("LAV", 37, 0, 0, -1) }, "V-T1", 1, 0, namer: namer, floor: "3");
        Show("Regular, floor 2 (LAV 3\" over)", new List<F> { Fx("WC", 0, 15.5, 0, -1), Fx("LAV", 40, 0, 0, -1) }, "same V-T1 at the floor 3 spot", 1, 0, namer: namer, floor: "2");

        // the toilet's waste stack already placed (Extract Fixtures) 1/2" off where the rule puts it, and a vent already there
        Show("Regular, waste + toilet vent in the model", new List<F> { Fx("WC", 0, 15.5, 0, -1), Fx("LAV", 37, 0, 0, -1) },
             "vent touches the model's G6; toilet vent skipped (in the model)", 1, 0,
             existing: new List<VentFinder.Existing> { new VentFinder.Existing { X = -3.75 / 12, Y = 0, System = "Sanitary", Size = 6, Name = "S-T4" },
                                                       new VentFinder.Existing { X = 1.5 / 12, Y = 0, System = "Vent", Size = 4, Name = "V-WC" } });

        // island kitchen sink (no wall: the faucet edge is its back)
        var island = Fx("KS", 100, 100, 0, 1); island.Island = true;
        Show("Island sink", new List<F> { island }, "Y4 past the hot water's place", 1, 0, 100, 100);
    }
}
