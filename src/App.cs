using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SleevesOpenings.Rules;

namespace SleevesOpenings
{
    /// <summary>
    /// Sleeves & Openings add-in entry point: builds the ribbon and holds the active rule set.
    /// </summary>
    public class App : IExternalApplication
    {
        public const string TabName = "Sleeves & Openings";

        private static RuleSet _rules;
        private static string _rulesModelPath;
        public static string LogPath { get; private set; }

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                LogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                       "SleevesOpenings", "logs");
                Directory.CreateDirectory(LogPath);
                Log("Starting");

                BuildRibbon(application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log("Startup failed: " + ex);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        /// <summary>Rules for the given document (project override > user > default). Cached per model path.</summary>
        public static RuleSet Rules(Document doc)
        {
            var modelPath = doc?.PathName;
            if (_rules == null || _rulesModelPath != modelPath)
                ReloadRules(doc);
            return _rules;
        }

        public static RuleSet ReloadRules(Document doc)
        {
            _rulesModelPath = doc?.PathName;
            _rules = RuleLoader.Load(_rulesModelPath);
            Log("Rules loaded from " + _rules.SourcePath);
            return _rules;
        }

        private static void BuildRibbon(UIControlledApplication app)
        {
            try { app.CreateRibbonTab(TabName); } catch { /* already exists */ }
            string asm = Assembly.GetExecutingAssembly().Location;

            var setup = app.CreateRibbonPanel(TabName, "Setup");
            setup.AddItem(Button("ProjectSetup", "Project\nSetup", asm, typeof(Commands.ProjectSetupCommand),
                "Classify levels, run the 'always verify' checklist, load families and set view ranges per the manual."));
            setup.AddItem(Button("ReloadRules", "Reload\nRules", asm, typeof(Commands.ReloadRulesCommand),
                "Re-read rules.json and show the active sizes/clearances."));
            setup.AddItem(Button("EditRules", "Edit\nRules", asm, typeof(Commands.EditRulesCommand),
                "Open rules.json for editing."));
            setup.AddItem(Button("MapFamilies", "Map\nFamilies", asm, typeof(Commands.MapFamiliesCommand),
                "Choose which loaded families and parameters are used for Regular Opening, Pipe Reference Opening, Round Sleeve and Electrical Opening."));
            setup.AddItem(Button("TestFamilies", "Create Test\nFamilies", asm, typeof(Commands.CreateTestFamiliesCommand),
                "No office families yet? Generates a parametric test opening and test sleeve, loads them and maps them so every Place button works."));

            // Place: one button per manual section. Click a plan, enter the engineer's size, click locations, Esc.
            var mech = app.CreateRibbonPanel(TabName, "Place – Mechanical");
            mech.AddStackedItems(
                Button("Exhaust", "Exhaust", asm, typeof(Commands.PlaceExhaustCommand), "Regular Opening = duct + 2\" each side; +4\" total on roof."),
                Button("GarbageChute", "Garbage Chute", asm, typeof(Commands.PlaceGarbageChuteCommand), "Always 28.5\" x 28.5\". Never resized, never offset."),
                Button("DryerExhaust", "Dryer Exhaust", asm, typeof(Commands.PlaceDryerExhaustCommand), "4\" round named DE; 6\" x 6\" opening on the roof."));
            mech.AddStackedItems(
                Button("Damper", "Motorized Damper", asm, typeof(Commands.PlaceMotorizedDamperCommand), "Damper + 2\" each side."),
                Button("Refrigeration", "Refrigeration", asm, typeof(Commands.PlaceRefrigerationCommand), "Pipe Reference Opening, Down Height = 0; +3\" W/L on roof."));

            var plumb = app.CreateRibbonPanel(TabName, "Place – Plumbing / FP");
            plumb.AddStackedItems(
                Button("Storm", "Storm", asm, typeof(Commands.PlaceStormCommand), "Sleeve = pipe + 2\"."),
                Button("AreaDrain", "Area Drain (x2)", asm, typeof(Commands.PlaceAreaDrainCommand), "Always two 10\" sleeves (primary + overflow) at 2'-0\" c-c."),
                Button("Condensate", "Condensate", asm, typeof(Commands.PlaceCondensateCommand), "All condensate sleeves are 3\"."));
            plumb.AddStackedItems(
                Button("Standpipe", "Standpipe", asm, typeof(Commands.PlaceStandpipeCommand), "Sleeve = pipe + 2\"; 5½\" to wall, 8\" c-c."),
                Button("Bathtub", "Bathtub", asm, typeof(Commands.PlaceBathtubCommand), "Two 6\" or one 10\" — per project decision."));

            // Panels for upcoming features are created here so the layout is stable.
            app.CreateRibbonPanel(TabName, "Risers");
            app.CreateRibbonPanel(TabName, "Check");
        }

        private static PushButtonData Button(string name, string text, string asm, Type cmd, string tooltip) =>
            new PushButtonData(name, text, asm, cmd.FullName) { ToolTip = tooltip };

        public static void Log(string msg)
        {
            try
            {
                File.AppendAllText(Path.Combine(LogPath, DateTime.Now.ToString("yyyy-MM-dd") + ".log"),
                                   $"[{DateTime.Now:HH:mm:ss}] {msg}\n");
            }
            catch { }
        }
    }
}
