using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using SleevesOpenings.Automation.Assembly;

namespace SleevesOpenings.Automation
{
    /// <summary>
    /// Turns a fixture name read from a plan (WC, LAV, BT...) into a safe sleeve point only when the
    /// corresponding Revit fixture can be identified unambiguously and exposes a sanitary pipe connector.
    /// Plan text is deliberately used only to select the nearby fixture; it is never used as the sleeve centre.
    /// </summary>
    public static class FixtureSleeveLocator
    {
        private const double MatchRadiusFeet = 6.0;
        private const double ClearNearestFeet = 1.0;

        private class Fixture
        {
            public string Name, Source, Level;
            public XYZ Point;
            public List<XYZ> Drains = new List<XYZ>();
        }

        /// <summary>Resolve fixture-review crossings in place. Returns reader-friendly results for the check summary.</summary>
        public static List<string> Apply(Document host, RiserAssembly assembly, PlumbingRules rules)
        {
            var messages = new List<string>();
            if (host == null || assembly == null || rules == null ||
                !string.Equals(rules.FixtureSleeves, "model", StringComparison.OrdinalIgnoreCase)) return messages;

            var fixtures = ReadFixtures(host);
            int placed = 0, noMatch = 0, ambiguous = 0, noDrain = 0;
            foreach (var c in assembly.Crossings.Where(IsFixtureReview))
            {
                if (!rules.Fixtures.TryGetValue(c.Tag ?? "", out var sleeve)) continue;
                var candidates = fixtures.Where(f => string.Equals(f.Level, c.Level, StringComparison.OrdinalIgnoreCase) && Matches(c.Tag, sleeve, f.Name))
                                         .Select(f => new { Fixture = f, Distance = Dist(c.X, c.Y, f.Point) })
                                         .Where(x => x.Distance <= MatchRadiusFeet)
                                         .OrderBy(x => x.Distance).ToList();
                if (candidates.Count == 0) { noMatch++; c.Notes.Add($"no matching Revit fixture found within {MatchRadiusFeet:0} ft of the plan label; review required"); continue; }
                if (candidates.Count > 1 && candidates[1].Distance - candidates[0].Distance < ClearNearestFeet)
                {
                    ambiguous++; c.Notes.Add("more than one matching Revit fixture is near the plan label; review required"); continue;
                }

                var found = candidates[0].Fixture;
                if (found.Drains.Count == 0)
                {
                    noDrain++; c.Notes.Add($"matched Revit fixture '{found.Name}' ({found.Source}) has no sanitary connector; review required"); continue;
                }

                int wanted = Math.Max(1, sleeve.Count);
                // Multiple sleeves below one fixture need a project-specific layout rule (for example a tub's two
                // sleeves).  A connector alone does not state which side/orientation the office expects, so retain
                // these for review instead of making a plausible-looking but wrong layout.
                if (wanted != 1)
                {
                    noDrain++; c.Notes.Add($"matched Revit fixture '{found.Name}' needs {wanted} sleeve point(s); the multi-sleeve layout requires review"); continue;
                }
                if (found.Drains.Count != wanted)
                {
                    noDrain++; c.Notes.Add($"matched Revit fixture '{found.Name}' has {found.Drains.Count} sanitary connector(s); {wanted} sleeve point(s) are required; review required"); continue;
                }

                c.X = found.Drains[0].X; c.Y = found.Drains[0].Y;
                c.Status = Crossing.Place;
                c.Confidence = "high";
                c.Notes.RemoveAll(n => n.Contains("position is the engineer's fixture text"));
                c.Notes.Add($"position from sanitary connector of Revit fixture '{found.Name}' ({found.Source}), {candidates[0].Distance * 12:0.#}\" from the plan label");
                placed++;
            }
            if (placed + noMatch + ambiguous + noDrain > 0)
                messages.Add($"Fixture sleeves: {placed} resolved from Revit sanitary connectors; {noMatch} fixture label(s) had no nearby match, {ambiguous} ambiguous, {noDrain} without matching connector(s) — left for review.");
            return messages;
        }

        private static bool IsFixtureReview(Crossing c) => c.Status == Crossing.Review && c.Confidence == "low" && c.Tag != null &&
            c.From.Any(x => x.IndexOf("fixture '", StringComparison.OrdinalIgnoreCase) >= 0);

        private static double Dist(double x, double y, XYZ p)
        {
            double dx = x - p.X, dy = y - p.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static bool Matches(string code, FixtureSleeve sleeve, string name)
        {
            string pattern = code.ToUpperInvariant() switch
            {
                "WC" => @"toilet|water.?closet|\bwc\b",
                "LAV" => @"lavatory|\blav\b",
                "BT" => @"bathtub|\btub\b",
                "KS" => @"kitchen.?sink|\bks\b",
                "LS" => @"laundry.?sink|\bls\b",
                "W/D" => @"washer|washing.?machine|\bw/d\b|\bwd\b",
                "WD" => @"washer|washing.?machine|\bwd\b",
                "SH" => @"shower",
                "FD" => @"floor.?drain|\bfd\b",
                _ => Regex.Escape(sleeve?.Name ?? code)
            };
            return Regex.IsMatch(name ?? "", pattern, RegexOptions.IgnoreCase);
        }

        private static List<Fixture> ReadFixtures(Document host)
        {
            var result = Read(host, Transform.Identity, "this model");
            foreach (var link in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                var linked = link.GetLinkDocument();
                if (linked != null) result.AddRange(Read(linked, link.GetTotalTransform(), $"link '{link.Name}'"));
            }
            return result;
        }

        private static List<Fixture> Read(Document doc, Transform transform, string source)
        {
            var result = new List<Fixture>();
            foreach (var category in new[] { BuiltInCategory.OST_PlumbingFixtures, BuiltInCategory.OST_Casework, BuiltInCategory.OST_GenericModel, BuiltInCategory.OST_MechanicalEquipment })
            foreach (var instance in new FilteredElementCollector(doc).OfCategory(category).WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                var box = instance.get_BoundingBox(null);
                var local = (instance.Location as LocationPoint)?.Point ?? (box == null ? null : (box.Min + box.Max) / 2);
                if (local == null) continue;
                var fixture = new Fixture
                {
                    Name = $"{instance.Symbol?.Family?.Name} {instance.Symbol?.Name} {instance.Name}",
                    Source = source,
                    Level = (doc.GetElement(instance.LevelId) as Level)?.Name,
                    Point = transform.OfPoint(local)
                };
                var manager = instance.MEPModel?.ConnectorManager;
                if (manager != null)
                    foreach (Connector connector in manager.Connectors)
                    {
                        if (connector.Domain != Domain.DomainPiping || connector.ConnectorType != ConnectorType.End) continue;
                        if (connector.PipeSystemType.ToString().IndexOf("Sanitary", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        fixture.Drains.Add(transform.OfPoint(connector.Origin));
                    }
                result.Add(fixture);
            }
            return result;
        }
    }
}
