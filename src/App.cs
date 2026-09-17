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
                UI.RevitWindow.MainHandle = application.MainWindowHandle;

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
            setup.AddItem(Button("Workflow", "Workflow", asm, typeof(Commands.WorkflowCommand),
                "The manual's sequence as a checklist: what is done in this model, what is next, one click to the right button."));
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

            var plan = app.CreateRibbonPanel(TabName, "Plan");
            plan.AddStackedItems(
                Button("Electrical", "Electrical Riser", asm, typeof(Commands.ElectricalCommand),
                    "Conduit calculator: apartments + roof = circles at 0.75\" c-c, recalculated at offsets; places the ELECTRIC opening on every floor, draws the circles, 2\" sleeve at roof."),
                Button("RefrigPlan", "Refrigeration Plan", asm, typeof(Commands.RefrigerationCommand),
                    "PTAC / split / VRF wizard: lines per stack, floors served, condenser roof; places Pipe Reference Openings on every floor (+3\" on the roof)."),
                Button("Roof", "Generate Roof", asm, typeof(Commands.RoofCommand),
                    "Copy the top floor's openings to the roof with roof sizes (+4\" exhaust, 6\"x6\" dryer, +3\" refrigeration, 2\" ELECTRIC) and auto-space them."));

            var risers = app.CreateRibbonPanel(TabName, "Risers");
            risers.AddItem(Button("Propagate", "Propagate\nRisers", asm, typeof(Commands.PropagateCommand),
                "Copy the selected openings floor by floor to each riser's termination level (rules 45-51). Same location, same size."));
            risers.AddItem(Button("RiserManager", "Riser\nManager", asm, typeof(Commands.RiserManagerCommand),
                "List every riser with its floors, sizes, gaps and offsets. Select or zoom to a riser in the model."));

            var check = app.CreateRibbonPanel(TabName, "Check");
            check.AddItem(Button("FinalCheck", "Final\nCheck", asm, typeof(Commands.FinalCheckCommand),
                "Audit every opening against the manual: sizes, clearances, shear walls/beams, wall edges, riser continuity, AD pairs, naming."));

            var docs = app.CreateRibbonPanel(TabName, "Document");
            docs.AddStackedItems(
                Button("Schedule", "Schedule", asm, typeof(Commands.ScheduleCommand),
                    "Create a Sleeves & Openings schedule per category: level, riser, system, size, family, comments."),
                Button("Tag", "Tag Openings", asm, typeof(Commands.TagCommand),
                    "Tag every add-in opening in the active plan with the category's default tag."),
                Button("SyncParams", "Sync Parameters", asm, typeof(Commands.SyncParametersCommand),
                    "Fill SO System / SO Riser / SO Size (shared parameters) on every opening from the add-in stamps."));
        }

        private static PushButtonData Button(string name, string text, string asm, Type cmd, string tooltip)
        {
            var data = new PushButtonData(name, text, asm, cmd.FullName) { ToolTip = tooltip };
            data.LargeImage = Icon(name, 32);
            data.Image = Icon(name, 16);
            return data;
        }

        /// <summary>Icons ship as Icons\{name}{px}.png next to the DLL (generated by tools/make_icons.py).</summary>
        private static System.Windows.Media.ImageSource Icon(string name, int px)
        {
            try
            {
                var path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "Icons", $"{name}{px}.png");
                if (!File.Exists(path)) return null;
                var img = new System.Windows.Media.Imaging.BitmapImage();
                img.BeginInit();
                img.UriSource = new Uri(path, UriKind.Absolute);
                img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch (Exception ex) { Log($"Icon {name}{px} failed: {ex.Message}"); return null; }
        }

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
