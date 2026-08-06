using System;
using System.IO;
using System.Windows.Forms;

namespace MsfsAirportPreloader
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            string iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "preloader.ini");
            Config config = Config.Load(iniPath);

            StartupRegistry.Apply(config.StartWithWindows, null);

            using var engine = new Engine(config, iniPath);
            engine.Start();

            Application.Run(new MainForm(engine, iniPath));
        }
    }
}
