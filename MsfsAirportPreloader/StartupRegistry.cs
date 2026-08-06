using System;
using System.Reflection;
using Microsoft.Win32;

namespace MsfsAirportPreloader
{
    /// <summary>Manages the "start at Windows login" entry (HKCU Run key).</summary>
    internal static class StartupRegistry
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MsfsAirportPreloader";

        public static void Apply(bool enabled, Action<string> log)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                if (key == null)
                {
                    return;
                }

                if (enabled)
                {
                    string exePath = Assembly.GetEntryAssembly()?.Location;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        key.SetValue(ValueName, $"\"{exePath}\"");
                    }
                }
                else
                {
                    if (key.GetValue(ValueName) != null)
                    {
                        key.DeleteValue(ValueName, throwOnMissingValue: false);
                    }
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Could not update startup entry: {ex.Message}");
            }
        }
    }
}
