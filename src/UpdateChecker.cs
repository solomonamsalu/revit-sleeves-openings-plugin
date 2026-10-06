using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Newtonsoft.Json.Linq;

namespace SleevesOpenings
{
    /// <summary>
    /// On startup reads latest.json from the update bucket in the background; when its major version is newer than
    /// this add-in's, shows a popup once Revit is idle with a link to download the installer.
    /// latest.json: { "version": "2.0.0", "url": "https://storage.googleapis.com/.../SleevesOpenings-Setup.exe" }
    /// </summary>
    internal static class UpdateChecker
    {
        /// <summary>Public Cloud Storage bucket holding latest.json and SleevesOpenings-Setup.exe.</summary>
        private const string LatestUrl = "https://storage.googleapis.com/sleeves-openings-updates/latest.json";

        private static Version _latest;
        private static string _downloadUrl;
        private static bool _done;

        public static void Start(UIControlledApplication app)
        {
            Task.Run(async () =>
            {
                try
                {
#if NETFRAMEWORK
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
#endif
                    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
                    {
                        // no-cache so a just-uploaded latest.json is seen, not the copy Google cached
                        http.DefaultRequestHeaders.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };
                        var json = JObject.Parse(await http.GetStringAsync(LatestUrl));
                        _downloadUrl = (string)json["url"];
                        _latest = new Version((string)json["version"]);
                    }
                }
                catch (Exception ex) { App.Log("Update check skipped: " + ex.Message); }
                finally { _done = true; }
            });
            app.Idling += OnIdling;
        }

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            if (!_done) return;
            ((UIApplication)sender).Idling -= OnIdling;
            if (_latest == null) return;

            var current = Assembly.GetExecutingAssembly().GetName().Version;
            App.Log($"Update check: installed {current}, latest {_latest}");
            if (_latest.Major <= current.Major) return;   // only a new major version is announced

            var td = new TaskDialog("Sleeves & Openings")
            {
                MainInstruction = $"Version {_latest.ToString(3)} is available",
                MainContent = $"You have {current.ToString(3)}. Download the installer, close Revit, then run it.",
                CommonButtons = TaskDialogCommonButtons.Close
            };
            if (!string.IsNullOrEmpty(_downloadUrl))
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Download the new version");
            if (td.Show() == TaskDialogResult.CommandLink1)
            {
                try { Process.Start(new ProcessStartInfo(_downloadUrl) { UseShellExecute = true }); }
                catch (Exception ex) { App.Log("Open download link failed: " + ex.Message); }
            }
        }
    }
}
