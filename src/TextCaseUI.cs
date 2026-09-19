using System;
using System.Drawing;
using System.Windows.Forms;

namespace TriSwitch
{
    public sealed partial class MainWindow
    {
        private TabPage BuildTextCase()
        {
            var page = new TabPage("Регистр текста") { Padding = new Padding(12), BackColor = Color.White };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Text = "Вставьте текст и выберите регистр. Выделен фрагмент — меняется только он;\r\nбез выделения — весь текст. Пробелы и переносы строк сохраняются." }, 0, 0);
            var editor = new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                HideSelection = false, MaxLength = 0, Font = new Font("Segoe UI", 13), AccessibleName = "Текст для изменения регистра" };
            layout.Controls.Add(editor, 0, 1);
            var modes = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            var undo = PreferenceButton("Отменить"); undo.Enabled = false;
            string previous = null; int previousStart = 0, previousLength = 0; bool changing = false;
            editor.TextChanged += delegate { if (!changing) { previous = null; undo.Enabled = false; } };
            string[] names = { "Каждое Слово С Заглавной", "ВСЕ ЗАГЛАВНЫЕ", "все строчные", "Предложения с заглавной" };
            foreach (TextCaseMode value in Enum.GetValues(typeof(TextCaseMode)))
            {
                TextCaseMode mode = value;
                var button = PreferenceButton(names[(int)mode]);
                button.FlatStyle = FlatStyle.Flat; button.UseVisualStyleBackColor = false;
                button.BackColor = Theme.TabBackground; button.ForeColor = Theme.Heading;
                button.FlatAppearance.BorderColor = Theme.AccentBorder;
                button.Click += delegate
                {
                    if (editor.Text.Length == 0) return;
                    Reset(); int start = editor.SelectionStart, length = editor.SelectionLength;
                    string input = length > 0 ? editor.SelectedText : editor.Text;
                    string result = TextCase.Convert(input, mode);
                    if (input == result) return;
                    previous = editor.Text; previousStart = start; previousLength = length;
                    changing = true;
                    try
                    {
                        if (length > 0) { editor.SelectedText = result; editor.Select(start, result.Length); }
                        else { editor.Text = result; editor.Select(Math.Min(start, result.Length), 0); }
                    }
                    finally { changing = false; }
                    undo.Enabled = true; editor.Focus();
                };
                modes.Controls.Add(button);
            }
            undo.Click += delegate
            {
                if (previous == null) return;
                string restore = previous; int start = previousStart, length = previousLength;
                editor.Text = restore; editor.Select(start, length); editor.Focus();
            };
            var paste = PreferenceButton("Вставить");
            paste.Click += delegate
            {
                try { if (Clipboard.ContainsText()) { editor.SelectedText = Clipboard.GetText(); editor.Focus(); } }
                catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(this, "Буфер обмена занят. Попробуйте ещё раз."); }
            };
            var copy = PreferenceButton("Копировать");
            copy.Click += delegate
            {
                try { string text = editor.SelectionLength > 0 ? editor.SelectedText : editor.Text; if (text.Length > 0) Clipboard.SetText(text); }
                catch (System.Runtime.InteropServices.ExternalException) { MessageBox.Show(this, "Буфер обмена занят. Попробуйте ещё раз."); }
            };
            actions.Controls.Add(paste); actions.Controls.Add(copy); actions.Controls.Add(undo);
            actions.Controls.Add(new Label { AutoSize = true, Margin = new Padding(3, 10, 3, 3), ForeColor = Theme.MutedText,
                Text = "Локально, без сохранения. Начало предложения — после . ! ? … или переноса строки." });
            layout.Controls.Add(modes, 0, 2); layout.Controls.Add(actions, 0, 3);
            page.Controls.Add(layout); return page;
        }
    }
}
