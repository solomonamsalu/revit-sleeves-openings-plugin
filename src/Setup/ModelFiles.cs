using System;
using Autodesk.Revit.DB;

namespace SleevesOpenings.Setup
{
    /// <summary>Where this model's project files are on disk.</summary>
    public static class ModelFiles
    {
        /// <summary>
        /// The model's place in the project folder, which is where its drawings sit next to it. A workshared model is
        /// opened as a local copy in the user's own Documents, far from the project, so the central model says where
        /// the project really is; everything else is the model's own path. Empty for a model never saved.
        /// </summary>
        public static string HomePath(Document doc)
        {
            if (doc == null) return null;
            try
            {
                if (doc.IsWorkshared)
                {
                    var central = doc.GetWorksharingCentralModelPath();
                    if (central != null)
                    {
                        string path = ModelPathUtils.ConvertModelPathToUserVisiblePath(central);
                        // a detached model keeps no central path, and a cloud model's is a URL, not a folder
                        if (!string.IsNullOrEmpty(path) && path.IndexOf("://", StringComparison.Ordinal) < 0) return path;
                    }
                }
            }
            catch (Exception) { }          // a server or cloud path the API cannot resolve: fall back to this file
            return doc.PathName;
        }
    }
}
