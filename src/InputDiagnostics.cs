using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Automation;

namespace TriSwitch
{
    // Explicit one-shot diagnostic. Never records field contents or selected text.
    internal static class InputDiagnostics
    {
        internal static int Run(string directory, bool checkTestTail = false)
        {
            var log = new List<string>();
            try
            {
                FocusStamp focus = Native.Focus();
                using (var process = Process.GetProcessById((int)focus.Process)) log.Add("Process=" + process.ProcessName);
                var name = new StringBuilder(128); Native.GetClassName(focus.Control, name, name.Capacity);
                log.Add("NativeClass=" + name);
                AutomationElement element = AutomationElement.FocusedElement;
                if (element == null) { log.Add("FocusedElement=unavailable"); return 1; }
                log.Add("ControlType=" + element.Current.ControlType.ProgrammaticName);
                log.Add("AutomationClass=" + element.Current.ClassName);
                log.Add("ProcessMatches=" + (element.Current.ProcessId == focus.Process));
                log.Add("HasKeyboardFocus=" + element.Current.HasKeyboardFocus);
                log.Add("IsEnabled=" + element.Current.IsEnabled);
                object password = element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true);
                log.Add("IsPassword=" + (password is bool ? password.ToString() : "unknown"));
                if (!(password is bool) || (bool)password) return 1;
                object pattern;
                bool hasValue = element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern);
                log.Add("ValuePattern=" + hasValue);
                if (hasValue) log.Add("ValueReadOnly=" + ((ValuePattern)pattern).Current.IsReadOnly);
                bool hasText = element.TryGetCurrentPattern(TextPattern.Pattern, out pattern);
                log.Add("TextPattern=" + hasText);
                if (hasText)
                {
                    var ranges = ((TextPattern)pattern).GetSelection();
                    log.Add("SelectionCount=" + (ranges == null ? -1 : ranges.Length));
                    if (ranges != null && ranges.Length == 1)
                    {
                        object readOnly = ranges[0].GetAttributeValue(TextPattern.IsReadOnlyAttribute);
                        log.Add("RangeReadOnly=" + (readOnly is bool ? readOnly.ToString() : "unknown"));
                    }
                }
                var guard = new FocusGuard(); guard.Configure(Settings.Load(Path.Combine(directory, "settings.json")).Exclusions);
                string identity; log.Add("GuardAllowsTyping=" + guard.TryCheck(focus, out identity));
                if (checkTestTail) log.Add("TestTailMatches=" + guard.TailMatches("ghbdtn "));
                log.Add("FocusUnchanged=" + focus.Same(Native.Focus()));
                return 0;
            }
            catch (Exception error) { log.Add("Error=" + error.GetType().Name); return 1; }
            finally { File.WriteAllLines(Path.Combine(directory, "input-diagnostics.txt"), log, Encoding.UTF8); }
        }
    }
}
