using System;
using System.Windows.Automation;

namespace TriSwitch
{
    internal static class FocusGuardTests
    {
        internal static void Run(Action<string, Action> test)
        {
            test("Editable Document caret works without ValuePattern", delegate
            {
                Tests.Check(FocusGuard.AllowsEditing(ControlType.Document, false, null, false),
                    "Word/contenteditable caret was rejected for ordinary typing");
            });
            test("Read-only Document cannot be enabled by another pattern", delegate
            {
                Tests.Check(!FocusGuard.AllowsEditing(ControlType.Document, false, null, true), "read-only document");
                Tests.Check(!FocusGuard.AllowsEditing(ControlType.Document, false, false, true), "read-only range overrides writable value");
                Tests.Check(!FocusGuard.AllowsEditing(ControlType.Document, false, true, false), "read-only value overrides writable range");
            });
            test("Document with unavailable caret or editability stays protected", delegate
            {
                Tests.Check(!FocusGuard.AllowsEditing(ControlType.Document, false, null, null),
                    "missing selection/pattern/IsReadOnly must not grant edit access");
            });
            test("Ordinary native and accessible edit fields remain supported", delegate
            {
                Tests.Check(FocusGuard.AllowsEditing(ControlType.Edit, false, null, null), "accessible edit");
                Tests.Check(FocusGuard.AllowsEditing(ControlType.Document, true, null, null), "native rich edit");
                Tests.Check(FocusGuard.AllowsEditing(ControlType.ComboBox, false, false, null), "writable value");
                Tests.Check(!FocusGuard.AllowsEditing(ControlType.Edit, true, true, null), "read-only native edit");
            });
            test("Text and pane elements do not become editable documents", delegate
            {
                Tests.Check(!FocusGuard.AllowsEditing(ControlType.Text, false, null, false), "static text");
                Tests.Check(!FocusGuard.AllowsEditing(ControlType.Pane, false, null, false), "container");
            });
        }
    }
}
