using System;

namespace TriSwitch
{
    public sealed partial class MainWindow
    {
        private SelectionCycle selectionCycle;
        private SelectionSnapshot selectedSnapshot;
        private string selectedUndoText;
        private Language selectedUndoLanguage;
        private bool selectionBusy;

        private void ClearSelectionCycle()
        {
            selectionCycle = null; selectedSnapshot = null; selectedUndoText = null; selectionBusy = false;
        }
        private bool CanUseSelection(FocusStamp focus, long serial)
        {
            return !paused && watcher != null && watcher.Serial == serial && !Native.ModifiersDown
                && focus.Same(Native.Focus()) && (focus.Window != Handle || focus.Control == TestEditor.Handle);
        }
        private void CycleSelection(FocusStamp focus)
        {
            long serial = pendingSerial;
            if (!CanUseSelection(focus, serial)) { Reset(); return; }
            SelectionSnapshot snapshot = SelectionSnapshot.Capture(focus, guard);
            if (snapshot == null)
            {
                Reset(); Notify("Выделите слово в доступном для редактирования поле. Поддерживается одна строка до 512 символов на EN/RU/UK."); return;
            }
            if (selectedSnapshot == null || !selectedSnapshot.Same(snapshot))
            {
                ClearSelectionCycle();
                Language? source = SelectionCycle.DetectSource(snapshot.Text, Native.InputLanguage(Native.GetKeyboardLayout(focus.Thread)), settings.CyrillicPriority);
                if (!source.HasValue) { Reset(); Notify("В выделении смешаны разные алфавиты. Выделите слово на одном языке."); return; }
                selectionCycle = new SelectionCycle(snapshot.Text, source.Value);
            }
            catalog.Refresh();
            Language target = SelectionCycle.Next(selectionCycle.Current, catalog.Available);
            if (target == selectionCycle.Current) { Notify("Для перебора нужна ещё одна установленная раскладка EN, RU или UK."); return; }
            string replacement = selectionCycle.TextFor(target, catalog.Convert);
            Language previous = selectionCycle.Current;
            ReplaceSelected(snapshot, replacement, target, serial, delegate(SelectionSnapshot changed)
            {
                selectedUndoText = snapshot.Text; selectedUndoLanguage = previous;
                selectionCycle.Current = target; selectedSnapshot = changed;
            });
        }
        private void UndoSelection()
        {
            SelectionSnapshot snapshot = selectedSnapshot;
            long serial = pendingSerial;
            if (snapshot == null || selectedUndoText == null || !CanUseSelection(snapshot.Focus, serial)
                || !snapshot.Same(SelectionSnapshot.Capture(snapshot.Focus, guard))) { Reset(); return; }
            ReplaceSelected(snapshot, selectedUndoText, selectedUndoLanguage, serial, delegate { ClearSelectionCycle(); });
        }
        private void ReplaceSelected(SelectionSnapshot snapshot, string replacement, Language target, long serial, Action<SelectionSnapshot> accepted)
        {
            Func<bool> current = () => CanUseSelection(snapshot.Focus, serial)
                && snapshot.Same(SelectionSnapshot.Capture(snapshot.Focus, guard)) && CanUseSelection(snapshot.Focus, serial);
            buffer.Clear(); undoAvailable = false;
            selectionBusy = true;
            if (!Native.ReplaceSelection(snapshot, replacement, current, () => CanUseSelection(snapshot.Focus, serial)))
            { Reset(); Notify("Не удалось заменить выделение. Проверьте текст в приложении."); return; }
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(700);
            Action verify = null;
            verify = delegate
            {
                if (!CanUseSelection(snapshot.Focus, serial)) { Reset(); return; }
                SelectionSnapshot changed = SelectionSnapshot.Capture(snapshot.Focus, guard);
                if (changed != null && changed.Identity == snapshot.Identity && changed.Text == replacement
                    && (!snapshot.NativeEditor || changed.Start == snapshot.Start))
                {
                    selectionBusy = false; accepted(changed); catalog.Switch(snapshot.Focus, target);
                    stateLabel.Text = "Выделение → " + Layouts.Names[(int)target] + ". " + settings.Hotkeys[6] + " — следующий вариант.";
                    return;
                }
                if (DateTime.UtcNow < deadline) { Schedule(verify, serial); return; }
                Reset(); Notify("Приложение не подтвердило замену и выделение. Проверьте текст перед повторным нажатием.");
            };
            if (snapshot.NativeEditor) verify(); else Schedule(verify, serial);
        }
    }
}
