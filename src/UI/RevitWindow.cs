using System;
using System.Windows.Forms;

namespace SleevesOpenings.UI
{
    /// <summary>
    /// Revit's main window as a WinForms owner. Every dialog is shown with this so it stays in front of
    /// Revit instead of disappearing behind it (which leaves the ribbon greyed out with nothing visible).
    /// </summary>
    public sealed class RevitWindow : IWin32Window
    {
        public static readonly RevitWindow Instance = new RevitWindow();

        /// <summary>Set once in App.OnStartup from UIControlledApplication.MainWindowHandle.</summary>
        public static IntPtr MainHandle { get; set; } = IntPtr.Zero;

        public IntPtr Handle => MainHandle != IntPtr.Zero
            ? MainHandle
            : System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
    }
}
