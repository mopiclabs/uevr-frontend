using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace UEVR
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        // Mopic: --attach without --show runs without a window (AttachSession); everything else opens MainWindow
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string? attachExe = null;
            bool showWindow = false;

            foreach (string arg in e.Args) {
                if (arg.StartsWith("--attach=")) {
                    attachExe = arg.Split('=')[1];
                } else if (arg == "--show") {
                    showWindow = true;
                }
            }

            if (attachExe != null && !showWindow) {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                new AttachSession(attachExe).Start();
                return;
            }

            new MainWindow().Show();
        }
    }
}
