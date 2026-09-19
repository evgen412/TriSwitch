using System;
using System.IO;
using Microsoft.Win32;

namespace TriSwitch
{
    internal static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "TriSwitch";
        internal static string CommandFor(string executable)
        {
            if (string.IsNullOrWhiteSpace(executable) || !Path.IsPathRooted(executable) || executable.Contains("\"") || executable.Contains("\r") || executable.Contains("\n"))
                throw new ArgumentException("Некорректный путь программы для автозапуска.");
            return "\"" + Path.GetFullPath(executable) + "\" --tray";
        }
        public static bool IsEnabled(string executable)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                return key != null && string.Equals(key.GetValue(ValueName) as string, CommandFor(executable), StringComparison.OrdinalIgnoreCase);
        }
        public static void SetEnabled(bool enabled, string executable)
        {
            string command = CommandFor(executable);
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null) throw new IOException("Не удалось открыть настройки автозапуска Windows.");
                if (enabled) key.SetValue(ValueName, command, RegistryValueKind.String);
                else if (string.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase)) key.DeleteValue(ValueName, false);
            }
        }
    }
}
