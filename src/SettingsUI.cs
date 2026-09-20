using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TriSwitch
{
    public sealed partial class MainWindow
    {
        private DataGridView replacementGrid;
        private Label hotkeyFeedback, replacementFeedback;
        private static Button PreferenceButton(string text)
        { return new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10, 5, 10, 5) }; }
        private void RefreshHotkeyHelp()
        {
            helpLabel.Text = string.Join("\r\n", settings.Hotkeys.Select((binding, i) => binding + "     " + HotkeyBinding.Actions[i]))
                + "\r\n\r\nНаберите слово здесь или в обычном текстовом поле.\r\nПосле пробела ещё можно исправить слово или отменить замену.";
        }
        private TabPage BuildHotkeys()
        {
            var page = new TabPage("Горячие клавиши") { BackColor = Color.White, Padding = new Padding(12) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Text = "Щёлкните поле и нажмите новое сочетание с Ctrl/Alt или F-клавишу, Pause.\r\nDelete / Backspace без модификаторов — отключить действие. Tab — следующее поле." }, 0, 0);
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7, AutoScroll = true };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            for (int i = 0; i < 6; i++)
            {
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                grid.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Text = HotkeyBinding.Actions[i], TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 8, 8, 8) }, 0, i);
                var input = new HotkeyBox(settings.Hotkeys[i]) { AccessibleName = HotkeyBinding.Actions[i], Margin = new Padding(3, 8, 3, 8) };
                input.Enter += delegate { Reset(); }; hotkeyBoxes.Add(input); grid.Controls.Add(input, 1, i);
            }
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.Controls.Add(grid, 0, 1);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            var save = PreferenceButton("Сохранить клавиши");
            save.Click += delegate
            {
                string error;
                bool ok = ApplyHotkeys(hotkeyBoxes.Select(b => b.Binding).ToArray(), out error);
                hotkeyFeedback.ForeColor = ok ? Color.DarkGreen : Color.Firebrick;
                hotkeyFeedback.Text = ok ? "Сохранено. Новые сочетания уже работают." : error;
            };
            var defaults = PreferenceButton("По умолчанию");
            defaults.Click += delegate { HotkeyBinding[] values = HotkeyBinding.Defaults(); for (int i = 0; i < 6; i++) hotkeyBoxes[i].Binding = values[i]; hotkeyFeedback.Text = "Нажмите «Сохранить клавиши», чтобы применить."; };
            buttons.Controls.Add(save); buttons.Controls.Add(defaults); layout.Controls.Add(buttons, 0, 2);
            hotkeyFeedback = new Label { AutoSize = true, Dock = DockStyle.Fill, Text = "Изменения применяются после сохранения. Занятые сочетания не заменят прежние." };
            layout.Controls.Add(hotkeyFeedback, 0, 3); page.Controls.Add(layout); return page;
        }
        internal bool ApplyHotkeys(HotkeyBinding[] values, out string error)
        {
            error = null;
            if (hotkeys == null) { error = "Обработка клавиатуры ещё не запущена."; return false; }
            Settings next = settings.Copy(); next.Hotkeys = values;
            if (!hotkeys.Apply(values, delegate { next.Save(settingsPath); }, out error)) return false;
            settings = next; Reset(); RefreshHotkeyHelp(); UpdateStatus(); return true;
        }
        private sealed class LayoutChoice
        {
            public int Id { get; set; }
            public string Name { get; set; }
        }
        private TabPage BuildReplacements()
        {
            var page = new TabPage("Свои замены") { BackColor = Color.White, Padding = new Padding(12) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Text = "После пробела: короткое слово → ваш текст. Например: brb → Скоро вернусь.\r\n«Сохранять регистр»: добрій → добрый, Добрій → Добрый, ДОБРІЙ → ДОБРЫЙ." }, 0, 0);
            replacementGrid = new DataGridView { Dock = DockStyle.Fill, BackgroundColor = Color.White, RowHeadersVisible = false,
                AllowUserToAddRows = true, AllowUserToDeleteRows = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = true, BorderStyle = BorderStyle.FixedSingle };
            replacementGrid.DefaultCellStyle.SelectionBackColor = Theme.Accent;
            replacementGrid.DefaultCellStyle.SelectionForeColor = Color.White;
            replacementGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Enabled", HeaderText = "Вкл.", FillWeight = 10 });
            replacementGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "From", HeaderText = "Что заменить", FillWeight = 23, MaxInputLength = 64 });
            replacementGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "To", HeaderText = "На что заменить", FillWeight = 42, MaxInputLength = 512 });
            replacementGrid.Columns.Add(new DataGridViewComboBoxColumn { Name = "Target", HeaderText = "Раскладка", FillWeight = 25, DisplayMember = "Name", ValueMember = "Id",
                DataSource = new[] { new LayoutChoice { Id = -1, Name = "Не менять" }, new LayoutChoice { Id = 0, Name = "EN" }, new LayoutChoice { Id = 1, Name = "RU" }, new LayoutChoice { Id = 2, Name = "UK" } } });
            replacementGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "PreserveCase", HeaderText = "Сохранять регистр", FillWeight = 20, MinimumWidth = 100 });
            replacementGrid.DefaultValuesNeeded += delegate(object sender, DataGridViewRowEventArgs e) { e.Row.Cells["Enabled"].Value = true; e.Row.Cells["Target"].Value = -1; };
            replacementGrid.DataError += delegate(object sender, DataGridViewDataErrorEventArgs e) { e.ThrowException = false; replacementFeedback.Text = "Выберите раскладку из списка."; };
            foreach (ReplacementRule rule in settings.Replacements) replacementGrid.Rows.Add(rule.Enabled, rule.From, rule.To, rule.Target, rule.PreserveCase);
            layout.Controls.Add(replacementGrid, 0, 1);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
            var save = PreferenceButton("Сохранить замены");
            save.Click += delegate
            {
                try
                {
                    if (!replacementGrid.EndEdit()) throw new ArgumentException("Завершите редактирование ячейки.");
                    var values = new List<ReplacementRule>();
                    foreach (DataGridViewRow row in replacementGrid.Rows)
                    {
                        if (row.IsNewRow) continue;
                        values.Add(new ReplacementRule { Enabled = Convert.ToBoolean(row.Cells["Enabled"].Value ?? true),
                            From = Convert.ToString(row.Cells["From"].Value).Trim(), To = Convert.ToString(row.Cells["To"].Value).Trim(), Target = Convert.ToInt32(row.Cells["Target"].Value ?? -1), PreserveCase = Convert.ToBoolean(row.Cells["PreserveCase"].Value ?? false) });
                    }
                    string error;
                    if (!ApplyReplacements(values, out error)) throw new ArgumentException(error);
                    replacementFeedback.ForeColor = Color.DarkGreen; replacementFeedback.Text = "Сохранено замен: " + values.Count + ". Они работают при включённых автозаменах.";
                }
                catch (Exception e) { replacementFeedback.ForeColor = Color.Firebrick; replacementFeedback.Text = e.Message; }
            };
            var add = PreferenceButton("Добавить"); add.Click += delegate { int row = replacementGrid.Rows.Add(true, "", "", -1); replacementGrid.CurrentCell = replacementGrid.Rows[row].Cells["From"]; replacementGrid.BeginEdit(true); };
            var remove = PreferenceButton("Удалить выбранные"); remove.Click += delegate { foreach (DataGridViewRow row in replacementGrid.SelectedRows.Cast<DataGridViewRow>().ToArray()) if (!row.IsNewRow) replacementGrid.Rows.Remove(row); };
            foreach (Button button in new[] { save, add, remove })
                Theme.HighlightButton(button);
            buttons.Controls.Add(save); buttons.Controls.Add(add); buttons.Controls.Add(remove); layout.Controls.Add(buttons, 0, 2);
            replacementFeedback = new Label { AutoSize = true, Dock = DockStyle.Fill, Text = "Срабатывают по пробелу. Исключения, пауза и отмена действуют и для своих замен." };
            layout.Controls.Add(replacementFeedback, 0, 3); page.Controls.Add(layout); return page;
        }
        internal bool ApplyReplacements(List<ReplacementRule> values, out string error)
        {
            error = null;
            try
            {
                var book = new ReplacementBook(values); Settings next = settings.Copy(); next.Replacements = values.Select(r => r.Copy()).ToList(); next.Save(settingsPath);
                settings = next; replacements = book; Reset(); return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }
    }
}
