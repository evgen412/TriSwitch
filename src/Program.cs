using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Forms;

namespace TriSwitch
{
    public sealed class FocusGuard
    {
        private uint lastPid;
        private string lastProcess;
        public HashSet<string> Excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public void Configure(string text)
        {
            Excluded = new HashSet<string>(text.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => Path.GetFileNameWithoutExtension(s.Trim())), StringComparer.OrdinalIgnoreCase);
        }
        public bool TryCheck(FocusStamp expected, out string identity)
        {
            identity = null;
            if (!expected.Same(Native.Focus())) return false;
            try
            {
                if (expected.Process != lastPid)
                {
                    using (Process p = Process.GetProcessById((int)expected.Process)) lastProcess = p.ProcessName;
                    lastPid = expected.Process;
                }
                if (Excluded.Contains(lastProcess)) return false;
                var className = new StringBuilder(128);
                Native.GetClassName(expected.Control, className, 128);
                string nativeClass = className.ToString();
                bool richEdit = nativeClass.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase);
                bool edit = richEdit || nativeClass.Equals("Edit", StringComparison.OrdinalIgnoreCase) || nativeClass.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase);
                if (edit && (Native.GetWindowLong(expected.Control, -16) & (0x20 | 0x800)) != 0) return false; // ES_PASSWORD / ES_READONLY
                AutomationElement element = AutomationElement.FocusedElement;
                if (element == null || element.Current.ProcessId != expected.Process) return false;
                object password = element.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true);
                if (!(password is bool) || (bool)password || !element.Current.IsEnabled || !element.Current.HasKeyboardFocus) return false;
                object pattern;
                bool valueEditable = element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern) && !((ValuePattern)pattern).Current.IsReadOnly;
                bool nativeEditable = edit && element.Current.NativeWindowHandle == expected.Control.ToInt64();
                if (!valueEditable && !nativeEditable && element.Current.ControlType != ControlType.Edit) return false;
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern) && ((ValuePattern)pattern).Current.IsReadOnly) return false;
                identity = string.Join(".", element.GetRuntimeId().Select(n => n.ToString()).ToArray());
                return expected.Same(Native.Focus());
            }
            catch (Exception) { return false; } // Fail closed if accessibility is unavailable.
        }
        public bool TailMatches(string expected)
        {
            try
            {
                AutomationElement element = AutomationElement.FocusedElement; object pattern;
                if (element == null || !element.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) return false;
                TextPatternRange[] selection = ((TextPattern)pattern).GetSelection();
                if (selection.Length != 1 || selection[0].CompareEndpoints(TextPatternRangeEndpoint.Start, selection[0], TextPatternRangeEndpoint.End) != 0) return false;
                TextPatternRange range = selection[0].Clone();
                range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -expected.Length);
                return range.GetText(expected.Length + 1) == expected;
            }
            catch (Exception) { return false; }
        }
    }

    public sealed partial class MainWindow : Form
    {
        private readonly string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        private Settings settings;
        private readonly string settingsPath;
        private ReplacementBook replacements;
        private HotkeyRegistry hotkeys;
        private readonly List<HotkeyBox> hotkeyBoxes = new List<HotkeyBox>();
        private readonly Label helpLabel = new Label();
        private readonly Detector detector;
        private readonly LayoutCatalog catalog = new LayoutCatalog();
        private readonly FocusGuard guard = new FocusGuard();
        private readonly WordBuffer buffer = new WordBuffer();
        private readonly NotifyIcon tray;
        private readonly System.Windows.Forms.Timer actionTimer = new System.Windows.Forms.Timer { Interval = 35 };
        private readonly System.Windows.Forms.Timer focusTimer = new System.Windows.Forms.Timer { Interval = 350 };
        private readonly Label stateLabel = new Label(), layoutLabel = new Label();
        private readonly CheckBox autoBox = new CheckBox();
        private readonly bool startHidden;
        private readonly bool testMode;
        internal TextBox TestEditor;
        internal bool IsWatching { get { return watcher != null; } }
        internal void SetTestExclusions(string text) { if (testMode) guard.Configure(text); }
        internal HotkeyBox FocusTestHotkey(int index)
        {
            var tabs = Controls[0].Controls.OfType<TabControl>().Single(); tabs.SelectedIndex = 2;
            hotkeyBoxes[index].Focus(); return hotkeyBoxes[index];
        }
        internal void FocusTestEditor() { Controls[0].Controls.OfType<TabControl>().Single().SelectedIndex = 0; TestEditor.Focus(); }
        internal string TestState { get { return "buffer=" + buffer.Word + "; valid=" + buffer.Valid + "; suffix=" + buffer.Suffix.Length + "; identity=" + currentIdentity + "; serial=" + watcher.Serial + "; pending=" + (pending != null) + "; status=" + stateLabel.Text; } }
        internal void FinishTest() { exiting = true; Close(); }
        private InputWatcher watcher;
        private FocusStamp currentFocus;
        private string currentIdentity;
        private bool paused, exiting;
        private bool allowWindow;
        private int queued;
        private Action pending;
        private long pendingSerial;
        private DateTime pendingDeadline;
        private string undoWord, undoSuffix;
        private Language undoLanguage;
        private bool undoAvailable;
        private ToolStripMenuItem pauseMenu;

        public MainWindow(bool hidden, bool testMode = false, bool previewOnly = false)
        {
            startHidden = hidden; this.testMode = testMode;
            settingsPath = Path.Combine(baseDirectory, testMode ? "test-preferences.json" : "settings.json");
            settings = testMode ? new Settings() : Settings.Load(settingsPath);
            detector = new Detector(Path.Combine(baseDirectory, "dictionaries"));
            Configure();
            Text = "TriSwitch · RU / UK / EN"; Size = new Size(780, 660); MinimumSize = new Size(740, 600);
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(245, 247, 251);
            AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
            ShowInTaskbar = !hidden;
            Icon = MakeIcon();
            BuildUI();
            var menu = new ContextMenuStrip();
            menu.Items.Add("Открыть TriSwitch", null, delegate { ShowWindow(); });
            pauseMenu = new ToolStripMenuItem("Приостановить", null, delegate { TogglePause(); }); menu.Items.Add(pauseMenu);
            menu.Items.Add("Выход", null, delegate { exiting = true; Close(); });
            tray = new NotifyIcon { Icon = Icon, Text = "TriSwitch · RU / UK / EN", ContextMenuStrip = menu, Visible = true };
            tray.DoubleClick += delegate { ShowWindow(); };
            actionTimer.Tick += delegate
            {
                if (pending == null) { actionTimer.Stop(); return; }
                if (watcher.Serial != pendingSerial || DateTime.UtcNow > pendingDeadline) { CancelPending(); return; }
                if (Native.ModifiersDown) return;
                Action action = pending; CancelPending(); action();
            };
            focusTimer.Tick += delegate { if (buffer.Valid && !currentFocus.Same(Native.Focus())) Reset(); };
            Shown += delegate { if (!previewOnly && !startHidden) StartWatcher(); };
            FormClosing += OnClosing;
            if (startHidden)
            {
                // Establish a message target without ever displaying the form.
                IntPtr windowHandle = Handle;
                BeginInvoke((Action)delegate { if (!previewOnly) StartWatcher(); });
            }
        }

        protected override void SetVisibleCore(bool value)
        { base.SetVisibleCore(startHidden && !allowWindow ? false : value); }
        internal bool TrayVisible { get { return tray.Visible; } }
        internal void OpenForTest() { ShowWindow(); }

        private static Icon MakeIcon()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bitmap))
            using (var brush = new SolidBrush(Theme.Accent))
            using (var font = new Font("Segoe UI", 18, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                g.Clear(Color.Transparent); g.FillEllipse(brush, 0, 0, 31, 31); g.DrawString("3", font, Brushes.White, 9, 3);
                IntPtr h = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); } finally { DestroyIcon(h); }
            }
        }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        private void Configure()
        {
            replacements = new ReplacementBook(settings.Replacements);
            guard.Configure(settings.Exclusions); detector.Ignored.Clear();
            foreach (string word in settings.IgnoreWords.Split(new[] { '\r', '\n', ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)) detector.Ignored.Add(word);
        }
        private void BuildUI()
        {
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 5 };
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.Controls.Add(new Label { Text = "Три языка. Одна клавиатура.", AutoSize = true, Font = new Font("Segoe UI", 22, FontStyle.Bold), ForeColor = Theme.Heading }, 0, 0);
            layoutLabel.Text = catalog.Status; layoutLabel.AutoSize = true; layoutLabel.ForeColor = Theme.MutedText; outer.Controls.Add(layoutLabel, 0, 1);
            autoBox.Text = "Автозамены по пробелу · словари и свои правила"; autoBox.AutoSize = true; autoBox.Checked = settings.Automatic;
            autoBox.CheckedChanged += delegate { settings.Automatic = autoBox.Checked; Reset(); SaveSettings(); UpdateStatus(); };
            outer.Controls.Add(autoBox, 0, 2);
            var tabs = new ContrastTabControl { Dock = DockStyle.Fill };
            var home = new TabPage("Как пользоваться") { Padding = new Padding(16), BackColor = Color.White };
            var homeLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            homeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); homeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); homeLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            helpLabel.Dock = DockStyle.Fill; helpLabel.AutoSize = true; RefreshHotkeyHelp();
            homeLayout.Controls.Add(helpLabel, 0, 0);
            homeLayout.Controls.Add(new Label { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(3, 12, 3, 12), Text = "Проверка: выберите EN и наберите ghbdtn или ghbdsn, затем пробел.\r\nРезультат: привет / привіт. Неоднозначные слова остаются как есть.", ForeColor = Theme.MutedText }, 0, 1);
            TestEditor = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 14), AccessibleName = "Поле проверки раскладок" };
            homeLayout.Controls.Add(TestEditor, 0, 2);
            home.Controls.Add(homeLayout); tabs.TabPages.Add(home);
            tabs.TabPages.Add(BuildConverter()); tabs.TabPages.Add(BuildHotkeys()); tabs.TabPages.Add(BuildReplacements()); tabs.TabPages.Add(BuildSettings());
            outer.Controls.Add(tabs, 0, 3);
            stateLabel.Dock = DockStyle.Fill; stateLabel.AutoSize = true; stateLabel.Margin = new Padding(3, 10, 3, 0); stateLabel.TextAlign = ContentAlignment.MiddleLeft; stateLabel.Font = new Font("Segoe UI", 9); outer.Controls.Add(stateLabel, 0, 4);
            Controls.Add(outer); UpdateStatus();
        }
        private TabPage BuildConverter()
        {
            var page = new TabPage("Конвертер текста") { Padding = new Padding(12), BackColor = Color.White };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, AutoSize = true, Text = "Вставьте текст и укажите раскладку, в которой он был набран.\r\nВыделение внутри этого поля преобразуется отдельно; без выделения — весь текст." }, 0, 0);
            var editor = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 13), AccessibleName = "Текст для ручного преобразования" };
            layout.Controls.Add(editor, 0, 1);
            var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            row.Controls.Add(new Label { Text = "Из:", AutoSize = true, Margin = new Padding(0, 9, 0, 0) });
            var source = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 }; source.Items.AddRange(Layouts.Names); source.SelectedIndex = 0; row.Controls.Add(source);
            foreach (Language language in Enum.GetValues(typeof(Language)))
            {
                Language target = language;
                var button = new Button { Text = "→ " + Layouts.Names[(int)target], AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 5, 12, 5) };
                button.Click += delegate
                {
                    Reset(); bool selected = editor.SelectionLength > 0; string value = selected ? editor.SelectedText : editor.Text;
                    string result = catalog.Convert(value, (Language)source.SelectedIndex, target);
                    if (selected) editor.SelectedText = result; else { editor.Text = result; source.SelectedIndex = (int)target; }
                }; row.Controls.Add(button);
            }
            var copy = new Button { Text = "Копировать", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 5, 12, 5) };
            copy.Click += delegate { try { if (editor.Text.Length > 0) Clipboard.SetText(editor.Text); } catch (Exception) { MessageBox.Show(this, "Буфер обмена занят. Попробуйте ещё раз."); } };
            row.Controls.Add(copy); layout.Controls.Add(row, 0, 2);
            layout.Controls.Add(new Label { Text = "Текст обрабатывается локально и не сохраняется.", Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.DimGray }, 0, 3);
            page.Controls.Add(layout); return page;
        }
        private TabPage BuildSettings()
        {
            var page = new TabPage("Настройки") { Padding = new Padding(12), BackColor = Color.White };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var startup = new CheckBox { AutoSize = true, Text = "Запускать при входе в Windows · сразу в трей", Margin = new Padding(3, 3, 3, 16) };
            try { startup.Checked = StartupRegistration.IsEnabled(Application.ExecutablePath); }
            catch (Exception) { startup.Checked = false; }
            bool updatingStartup = false;
            startup.CheckedChanged += delegate
            {
                if (updatingStartup) return;
                try
                {
                    if (!testMode) StartupRegistration.SetEnabled(startup.Checked, Application.ExecutablePath);
                    stateLabel.Text = startup.Checked ? "Автозапуск включён для текущего пользователя Windows." : "Автозапуск выключен.";
                }
                catch (Exception error)
                {
                    updatingStartup = true; startup.Checked = !startup.Checked; updatingStartup = false;
                    MessageBox.Show(this, "Не удалось изменить автозапуск:\r\n" + error.Message, "TriSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            layout.Controls.Add(startup, 0, 0); layout.SetColumnSpan(startup, 2);
            layout.Controls.Add(new Label { Text = "Не работать в программах\r\nИмя процесса, по одному на строку", Dock = DockStyle.Fill, AutoSize = true }, 0, 1);
            layout.Controls.Add(new Label { Text = "Не исправлять слова автоматически\r\nПо одному на строку", Dock = DockStyle.Fill, AutoSize = true }, 1, 1);
            var programs = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = settings.Exclusions };
            var words = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = settings.IgnoreWords };
            layout.Controls.Add(programs, 0, 2); layout.Controls.Add(words, 1, 2);
            var save = new Button { Text = "Сохранить", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 5, 12, 5) };
            save.Click += delegate { settings.Exclusions = programs.Text; settings.IgnoreWords = words.Text; Configure(); Reset(); if (SaveSettings()) stateLabel.Text = "Исключения сохранены."; };
            layout.Controls.Add(save, 0, 3);
            var refresh = new Button { Text = "Обновить список раскладок", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 5, 12, 5) };
            refresh.Click += delegate { catalog.Refresh(); layoutLabel.Text = catalog.Status; }; layout.Controls.Add(refresh, 1, 3);
            page.Controls.Add(layout); return page;
        }
        private bool SaveSettings()
        {
            try { settings.Save(settingsPath); return true; }
            catch (Exception e) { MessageBox.Show(this, "Не удалось сохранить настройки рядом с программой:\r\n" + e.Message, "TriSwitch"); return false; }
        }
        private void StartWatcher()
        {
            if (watcher != null) return;
            try
            {
                watcher = new InputWatcher(delegate(KeyEvent e)
                {
                    if (Interlocked.Increment(ref queued) > 256) { Interlocked.Decrement(ref queued); return; }
                    try { BeginInvoke((Action)delegate { Interlocked.Decrement(ref queued); OnKey(e); }); }
                    catch (InvalidOperationException) { Interlocked.Decrement(ref queued); }
                }, testMode);
                hotkeys = new HotkeyRegistry((id, binding) => Native.RegisterHotKey(Handle, id, binding.Modifiers | 0x4000u, binding.Key), id => Native.UnregisterHotKey(Handle, id));
                var failures = hotkeys.Start(settings.Hotkeys);
                focusTimer.Start();
                if (failures.Count > 0) MessageBox.Show(this, "Эти сочетания заняты другой программой:\r\n" + string.Join(", ", failures) + "\r\nОстальные функции доступны.", "TriSwitch");
            }
            catch (Exception e) { paused = true; UpdateStatus(); MessageBox.Show(this, "Не удалось включить обработку клавиатуры:\r\n" + e.Message, "TriSwitch"); }
        }
        private void OnKey(KeyEvent e)
        {
            if (exiting) return;
            if (e.Reset) { Reset(); return; }
            if (hotkeys != null && hotkeys.Matches(e)) return;
            CancelPending();
            if (paused || e.Ctrl || e.Alt || e.Win) { Reset(); return; }
            // Never auto-expand text while the user is editing preferences.
            if (e.Focus.Window == Handle && e.Focus.Control != TestEditor.Handle) { Reset(); return; }
            // Never replay a backlogged stream into a different field.
            if (watcher.Serial - e.Serial > 32) { Reset(); return; }
            Language? language = Layouts.FromHandle(e.Layout);
            string identity;
            if (!language.HasValue || !guard.TryCheck(e.Focus, out identity)) { Reset(); return; }
            if (!currentFocus.Same(e.Focus) || currentIdentity != identity) Reset();
            currentFocus = e.Focus; currentIdentity = identity;
            if (e.Vk == 8) { undoAvailable = false; buffer.Backspace(); return; }
            if (e.Vk == 32)
            {
                if (e.Shift) { Reset(); return; }
                buffer.Space();
                if (undoAvailable) { undoSuffix = buffer.Suffix; if (!buffer.Valid) undoAvailable = false; }
                if (buffer.Valid && settings.Automatic && !undoAvailable && watcher.Serial == e.Serial)
                {
                    Suggestion suggestion = replacements.Find(buffer.Word, buffer.Language, detector.Ignored) ?? detector.Suggest(buffer.Word, buffer.Language, catalog.Convert);
                    if (suggestion != null && (suggestion.PreserveLayout || catalog.Available(suggestion.Language)))
                        Schedule(delegate { ReplaceWord(suggestion.Text, suggestion.Language, true, suggestion.PreserveLayout, suggestion.Custom); }, e.Serial);
                }
                return;
            }
            if (e.Vk == 9 || e.Vk == 13 || e.Vk == 27 || (e.Vk >= 33 && e.Vk <= 46) || (e.Vk >= 112 && e.Vk <= 135) || e.Vk == 91 || e.Vk == 92) { Reset(); return; }
            if (e.Vk == 20) return;
            var state = new byte[256]; if (e.Shift) state[16] = 0x80; if (e.Caps) state[20] = 1;
            var text = new StringBuilder(8);
            int count = Native.ToUnicodeEx(e.Vk, e.Scan, state, text, 8, 4, e.Layout);
            if (count != 1 || char.IsControl(text[0]) || char.IsSurrogate(text[0])) { Reset(); return; }
            undoAvailable = false; buffer.Append(text[0], language.Value);
        }
        private void Schedule(Action action, long serial = -1)
        {
            if (watcher == null) return;
            pending = action; pendingSerial = serial < 0 ? watcher.Serial : serial; pendingDeadline = DateTime.UtcNow.AddSeconds(1.5); actionTimer.Start();
        }
        private void CancelPending() { pending = null; actionTimer.Stop(); }
        private void Reset() { buffer.Clear(); undoAvailable = false; currentIdentity = null; CancelPending(); }
        private bool CanReplace()
        {
            string identity;
            return !paused && buffer.Valid && !Native.ModifiersDown && currentFocus.Same(Native.Focus()) && guard.TryCheck(currentFocus, out identity) && identity == currentIdentity
                && guard.TailMatches(buffer.Word + buffer.Suffix) && currentFocus.Same(Native.Focus()) && watcher.Serial == pendingSerial;
        }
        private void ReplaceWord(string replacement, Language target, bool automatic, bool preserveLayout = false, bool custom = false)
        {
            if (!CanReplace()) { Reset(); return; }
            if (!preserveLayout && !catalog.Available(target)) { Notify("Раскладка " + Layouts.Names[(int)target] + " не установлена в Windows."); return; }
            string old = buffer.Word, suffix = buffer.Suffix; Language oldLanguage = buffer.Language;
            if (!Native.Replace(old.Length + suffix.Length, replacement + suffix))
            { Reset(); Notify("Windows не приняла замену. Проверьте текст: приложение может работать с правами администратора."); return; }
            undoWord = old; undoSuffix = suffix; undoLanguage = oldLanguage; undoAvailable = true;
            buffer.Word = replacement; buffer.Language = target;
            if (!preserveLayout) catalog.Switch(currentFocus, target);
            stateLabel.Text = (custom ? "Своя замена" : automatic ? "Автоисправление" : "Замена") + " → " + Layouts.Names[(int)target]
                + (settings.Hotkeys[4].Key == 0 ? ". Горячая клавиша отмены отключена." : ". " + settings.Hotkeys[4] + " — отмена.");
        }
        private void Undo()
        {
            if (!undoAvailable || !CanReplace()) { Reset(); return; }
            if (!Native.Replace(buffer.Word.Length + buffer.Suffix.Length, undoWord + undoSuffix)) { Reset(); Notify("Windows не приняла отмену. Проверьте текст."); return; }
            // Do not repeat the same rejected automatic correction during this run.
            detector.Ignored.Add(undoWord.TrimEnd('.', ',', '!', '?', ':', ';'));
            buffer.Word = undoWord; buffer.Suffix = undoSuffix; buffer.Language = undoLanguage; undoAvailable = false;
            catalog.Switch(currentFocus, undoLanguage); stateLabel.Text = "Замена отменена. Это слово не будет автоисправляться до перезапуска.";
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x312)
            {
                uint key = (uint)((message.LParam.ToInt64() >> 16) & 0xffff), modifiers = (uint)(message.LParam.ToInt64() & 0xffff);
                HotkeyBox capture = hotkeyBoxes.FirstOrDefault(b => b.Focused);
                if (capture != null) { Reset(); capture.CaptureGesture(key, modifiers); return; }
                int id = hotkeys == null ? 0 : hotkeys.Resolve(message.WParam.ToInt32(), key, modifiers);
                if (id == 0) return;
                if (id == 6) TogglePause();
                else if (!paused)
                {
                    if (id == 5) Schedule(Undo);
                    else if (buffer.Valid)
                    {
                        Language target = id >= 1 && id <= 3 ? (Language)(id - 1) : (Language)(((int)buffer.Language + 1) % 3);
                        Schedule(delegate { ReplaceWord(catalog.Convert(buffer.Word, buffer.Language, target), target, false); });
                    }
                }
                return;
            }
            base.WndProc(ref message);
        }
        private void TogglePause() { paused = !paused; Reset(); UpdateStatus(); }
        private void UpdateStatus()
        {
            stateLabel.Text = paused ? "Пауза · " + (settings.Hotkeys[5].Key == 0 ? "Продолжить можно через меню значка в трее" : settings.Hotkeys[5] + " — продолжить") : "Работает локально · " + detector.Count.ToString("N0") + " словарных записей · Закрытие окна сворачивает в трей";
            if (pauseMenu != null) pauseMenu.Text = paused ? "Продолжить" : "Приостановить";
            if (tray != null) tray.Text = paused ? "TriSwitch · Пауза" : "TriSwitch · RU / UK / EN";
        }
        private void Notify(string text) { tray.ShowBalloonTip(4000, "TriSwitch", text, ToolTipIcon.Info); }
        private void ShowWindow() { allowWindow = true; ShowInTaskbar = true; Show(); WindowState = FormWindowState.Normal; Activate(); catalog.Refresh(); layoutLabel.Text = catalog.Status; }
        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            exiting = true; actionTimer.Stop(); focusTimer.Stop();
            if (hotkeys != null) hotkeys.Dispose();
            if (watcher != null) watcher.Dispose();
            tray.Visible = false; tray.Dispose(); actionTimer.Dispose(); focusTimer.Dispose();
        }
    }

    public static class Program
    {
        [STAThread]
        public static int Main(string[] args)
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--self-test")) return Tests.Run(AppDomain.CurrentDomain.BaseDirectory);
            if (args.Contains("--integration-test")) return IntegrationTests.Run(AppDomain.CurrentDomain.BaseDirectory);
            if (args.Contains("--ui-smoke-test")) return IntegrationTests.Preview(AppDomain.CurrentDomain.BaseDirectory);
            if (args.Contains("--startup-test")) return IntegrationTests.Startup(AppDomain.CurrentDomain.BaseDirectory);
            bool created;
            using (var mutex = new Mutex(true, "Local\\TriSwitch.3Languages", out created))
            {
                if (!created) { MessageBox.Show("TriSwitch уже работает. Откройте его через значок «3» рядом с часами.", "TriSwitch"); return 0; }
                try { Application.Run(new MainWindow(!args.Contains("--show"))); return 0; }
                catch (Exception e) { MessageBox.Show("Не удалось запустить TriSwitch:\r\n" + e.Message, "TriSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error); return 1; }
            }
        }
    }
}
