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
        public bool TryCheck(FocusStamp expected, out string identity, bool selectedText = false)
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
                bool documentEditable = false;
                if (selectedText && element.Current.ControlType == ControlType.Document && element.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                {
                    TextPatternRange[] ranges = ((TextPattern)pattern).GetSelection();
                    object readOnly = ranges.Length == 1 ? ranges[0].GetAttributeValue(TextPattern.IsReadOnlyAttribute) : null;
                    documentEditable = readOnly is bool && !(bool)readOnly;
                }
                if (!valueEditable && !nativeEditable && !documentEditable && element.Current.ControlType != ControlType.Edit) return false;
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
        private readonly NotepadFontGuard notepadFontGuard = new NotepadFontGuard();
        private readonly WordBuffer buffer = new WordBuffer();
        private readonly NotifyIcon tray;
        private readonly System.Windows.Forms.Timer actionTimer = new System.Windows.Forms.Timer { Interval = 35 };
        private readonly System.Windows.Forms.Timer focusTimer = new System.Windows.Forms.Timer { Interval = 350 };
        private readonly Label stateLabel = new Label(), layoutLabel = new Label();
        private readonly CheckBox autoBox = new CheckBox();
        private readonly bool startHidden;
        private readonly bool testMode;
        internal TextBox TestEditor;
        internal ComboBox TestCyrillicPriority;
        internal CheckBox TestSpellCheck;
        internal TextBox TestSpellingIgnoreWords;
        internal Button TestSaveExclusions;
        internal Action<string> TestTrace;
        private void TraceTest(string stage) { if (testMode && TestTrace != null) TestTrace(stage); }
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
                if (watcher.Serial != pendingSerial || DateTime.UtcNow > pendingDeadline) { Reset(); return; }
                if (Native.ModifiersDown) return;
                Action action = pending; CancelPending(); action();
            };
            focusTimer.Tick += delegate { var focus = Native.Focus(); notepadFontGuard.Protect(focus); if ((buffer.Valid && !currentFocus.Same(focus)) || (selectedSnapshot != null && !selectedSnapshot.Focus.Same(focus))) Reset(); };
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
            detector.SpellingIgnored.Clear();
            detector.SpellingIgnored.UnionWith(SpellChecker.ParseIgnoredWords(settings.SpellingIgnoreWords));
        }
        private void BuildUI()
        {
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 5 };
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            outer.Controls.Add(new Label { Text = "Три языка. Одна клавиатура.", AutoSize = true, Font = new Font("Segoe UI", 22, FontStyle.Bold), ForeColor = Theme.Heading }, 0, 0);
            layoutLabel.Text = catalog.Status; layoutLabel.AutoSize = true; layoutLabel.ForeColor = Theme.MutedText; outer.Controls.Add(layoutLabel, 0, 1);
            autoBox.Text = "Автозамены по пробелу · раскладка, орфография и свои правила"; autoBox.AutoSize = true; autoBox.Checked = settings.Automatic;
            autoBox.CheckedChanged += delegate { settings.Automatic = autoBox.Checked; Reset(); SaveSettings(); UpdateStatus(); };
            outer.Controls.Add(autoBox, 0, 2);
            var tabs = new ContrastTabControl { Dock = DockStyle.Fill };
            var home = new TabPage("Как пользоваться") { Padding = new Padding(16), BackColor = Color.White };
            var homeLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            homeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); homeLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); homeLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            helpLabel.Dock = DockStyle.Fill; helpLabel.AutoSize = true; RefreshHotkeyHelp();
            homeLayout.Controls.Add(helpLabel, 0, 0);
            homeLayout.Controls.Add(new Label { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(3, 12, 3, 12), Text = "Проверка: выберите EN и наберите ghbdtn, ghbdsn или yf, затем пробел.\r\nРезультат: привет / привіт / на. Приоритет RU/UK выбирается в настройках.", ForeColor = Theme.MutedText }, 0, 1);
            TestEditor = new TextBox { Multiline = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, Font = new Font("Segoe UI", 14), AccessibleName = "Поле проверки раскладок" };
            homeLayout.Controls.Add(TestEditor, 0, 2);
            home.Controls.Add(homeLayout); tabs.TabPages.Add(home);
            tabs.TabPages.Add(BuildConverter()); tabs.TabPages.Add(BuildHotkeys()); tabs.TabPages.Add(BuildReplacements()); tabs.TabPages.Add(BuildSettings()); tabs.TabPages.Add(BuildTextCase());
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
                Theme.HighlightButton(button);
                button.Click += delegate
                {
                    Reset(); bool selected = editor.SelectionLength > 0; string value = selected ? editor.SelectedText : editor.Text;
                    string result = catalog.Convert(value, (Language)source.SelectedIndex, target);
                    if (selected) editor.SelectedText = result; else { editor.Text = result; source.SelectedIndex = (int)target; }
                }; row.Controls.Add(button);
            }
            var copy = new Button { Text = "Копировать", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 5, 12, 5) };
            Theme.HighlightButton(copy);
            copy.Click += delegate { try { if (editor.Text.Length > 0) Clipboard.SetText(editor.Text); } catch (Exception) { MessageBox.Show(this, "Буфер обмена занят. Попробуйте ещё раз."); } };
            row.Controls.Add(copy); layout.Controls.Add(row, 0, 2);
            layout.Controls.Add(new Label { Text = "Текст обрабатывается локально и не сохраняется.", Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.DimGray }, 0, 3);
            page.Controls.Add(layout); return page;
        }
        private TabPage BuildSettings()
        {
            var page = new TabPage("Настройки") { Padding = new Padding(12), BackColor = Color.White };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
            var priorityPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 4, Margin = new Padding(0, 0, 0, 12) };
            priorityPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); priorityPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            priorityPanel.Controls.Add(new Label { Text = "Приоритет языка при неоднозначности:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            var priority = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175, Anchor = AnchorStyles.Left, AccessibleName = "Приоритет языка" };
            if (testMode) TestCyrillicPriority = priority;
            priority.Items.AddRange(new object[] { "Русский", "Украинский" });
            priority.SelectedIndex = settings.CyrillicPriority == Language.Ukrainian ? 1 : 0;
            bool updatingPriority = false;
            priority.SelectedIndexChanged += delegate
            {
                if (updatingPriority || priority.SelectedIndex < 0) return;
                string error;
                if (ApplyCyrillicPriority(priority.SelectedIndex == 1 ? Language.Ukrainian : Language.Russian, out error))
                    stateLabel.Text = "Приоритет языка: " + (settings.CyrillicPriority == Language.Ukrainian ? "украинский" : "русский") + ". Настройка сохранена.";
                else
                {
                    updatingPriority = true; priority.SelectedIndex = settings.CyrillicPriority == Language.Ukrainian ? 1 : 0; updatingPriority = false;
                    MessageBox.Show(this, "Не удалось сохранить приоритет языка:\r\n" + error, "TriSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            priorityPanel.Controls.Add(priority, 1, 0);
            var priorityHelp = new Label { Text = "Выбор между RU и UK применяется и сохраняется сразу. По умолчанию — русский.", AutoSize = true, Dock = DockStyle.Fill, ForeColor = Theme.MutedText };
            priorityPanel.Controls.Add(priorityHelp, 0, 1); priorityPanel.SetColumnSpan(priorityHelp, 2);
            var spelling = new CheckBox { Text = "Исправлять орфографию и опечатки после пробела", AutoSize = true,
                Checked = settings.SpellCheck, Margin = new Padding(3, 12, 3, 3), AccessibleName = "Исправление орфографии" };
            if (testMode) TestSpellCheck = spelling;
            bool updatingSpelling = false;
            spelling.CheckedChanged += delegate
            {
                if (updatingSpelling) return;
                string error;
                if (ApplySpellCheck(spelling.Checked, out error))
                    stateLabel.Text = "Исправление опечаток " + (settings.SpellCheck ? "включено" : "отключено") + ". Настройка сохранена.";
                else
                {
                    updatingSpelling = true; spelling.Checked = settings.SpellCheck; updatingSpelling = false;
                    MessageBox.Show(this, "Не удалось сохранить настройку орфографии:\r\n" + error, "TriSwitch", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            priorityPanel.Controls.Add(spelling, 0, 2); priorityPanel.SetColumnSpan(spelling, 2);
            var spellingHelp = new Label { Text = "Проверка на языке ввода. Сохраняется сразу; замена отменяется обычной клавишей отмены.", AutoSize = true, Dock = DockStyle.Fill, ForeColor = Theme.MutedText };
            priorityPanel.Controls.Add(spellingHelp, 0, 3); priorityPanel.SetColumnSpan(spellingHelp, 2);
            layout.Controls.Add(priorityPanel, 0, 1); layout.SetColumnSpan(priorityPanel, 2);
            layout.Controls.Add(new Label { Text = "Не работать в программах\r\nИмя процесса, по одному на строку", Dock = DockStyle.Fill, AutoSize = true }, 0, 2);
            layout.Controls.Add(new Label { Text = "Не исправлять слова · по одному на строку\r\nБез учёта регистра", Dock = DockStyle.Fill, AutoSize = true }, 1, 2);
            var programs = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = settings.Exclusions };
            var words = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Text = settings.IgnoreWords, AccessibleName = "Исключения для всех автозамен" };
            var spellingWords = new TextBox { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                Text = settings.SpellingIgnoreWords, AccessibleName = "Не исправлять орфографию", AcceptsReturn = true };
            var wordTabs = new ContrastTabControl { Dock = DockStyle.Fill };
            var allWordsPage = new TabPage("Все автозамены") { BackColor = Color.White, Padding = new Padding(3) };
            var spellingWordsPage = new TabPage("Орфография") { BackColor = Color.White, Padding = new Padding(3) };
            allWordsPage.Controls.Add(words); spellingWordsPage.Controls.Add(spellingWords);
            wordTabs.TabPages.Add(allWordsPage); wordTabs.TabPages.Add(spellingWordsPage); wordTabs.SelectedIndex = 1;
            layout.Controls.Add(programs, 0, 3); layout.Controls.Add(wordTabs, 1, 3);
            var save = new Button { Text = "Сохранить", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 5, 12, 5) };
            Theme.HighlightButton(save);
            save.Click += delegate
            {
                string error;
                stateLabel.Text = ApplyExclusions(programs.Text, words.Text, spellingWords.Text, out error)
                    ? "Исключения сохранены. Список «Орфография» пропускает только исправление опечаток."
                    : "Не удалось сохранить исключения: " + error;
            };
            if (testMode) { TestSpellingIgnoreWords = spellingWords; TestSaveExclusions = save; }
            layout.Controls.Add(save, 0, 4);
            var refresh = new Button { Text = "Обновить список раскладок", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12, 5, 12, 5) };
            Theme.HighlightButton(refresh);
            refresh.Click += delegate { catalog.Refresh(); layoutLabel.Text = catalog.Status; }; layout.Controls.Add(refresh, 1, 4);
            page.Controls.Add(layout); return page;
        }
        internal bool ApplyExclusions(string programs, string words, string spellingWords, out string error)
        {
            error = null;
            Settings next = settings.Copy(); next.Exclusions = programs; next.IgnoreWords = words; next.SpellingIgnoreWords = spellingWords;
            try { next.Save(settingsPath); }
            catch (Exception e) { error = e.Message; return false; }
            settings = next; Configure(); Reset(); return true;
        }
        internal bool ApplyCyrillicPriority(Language language, out string error)
        {
            error = null;
            Settings next = settings.Copy(); next.CyrillicPriority = language;
            try { next.Save(settingsPath); }
            catch (Exception e) { error = e.Message; return false; }
            settings = next; Reset(); UpdateStatus(); return true;
        }
        internal bool ApplySpellCheck(bool enabled, out string error)
        {
            error = null;
            Settings next = settings.Copy(); next.SpellCheck = enabled;
            try { next.Save(settingsPath); }
            catch (Exception e) { error = e.Message; return false; }
            settings = next; Reset(); UpdateStatus(); return true;
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
            notepadFontGuard.Protect(e.Focus);
            if (e.Reset) { Reset(); return; }
            if (hotkeys != null && hotkeys.Matches(e)) return;
            ClearSelectionCycle();
            CancelPending();
            if (paused || e.Ctrl || e.Alt || e.Win) { Reset(); return; }
            // Never auto-expand text while the user is editing preferences.
            if (e.Focus.Window == Handle && e.Focus.Control != TestEditor.Handle) { Reset(); return; }
            // Never replay a backlogged stream into a different field.
            if (watcher.Serial - e.Serial > 32) { Reset(); return; }
            Language? language = Native.InputLanguage(e.Layout);
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
                    Language preferred = catalog.Available(settings.CyrillicPriority) ? settings.CyrillicPriority
                        : settings.CyrillicPriority == Language.Russian ? Language.Ukrainian : Language.Russian;
                    Suggestion suggestion = replacements.Find(buffer.Word, buffer.Language, detector.Ignored) ?? detector.Suggest(buffer.Word, buffer.Language, catalog.Convert, preferred);
                    if (suggestion == null && settings.SpellCheck) suggestion = detector.SuggestSpelling(buffer.Word, buffer.Language);
                    if (suggestion != null && (suggestion.PreserveLayout || catalog.Available(suggestion.Language)))
                        Schedule(delegate { ReplaceWord(suggestion.Text, suggestion.Language, true, suggestion.PreserveLayout, suggestion.Custom, suggestion.Spelling); }, e.Serial);
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
        private void Reset() { buffer.Clear(); undoAvailable = false; currentIdentity = null; ClearSelectionCycle(); CancelPending(); }
        private bool CanReplace()
        {
            string identity;
            return !paused && buffer.Valid && !Native.ModifiersDown && currentFocus.Same(Native.Focus()) && guard.TryCheck(currentFocus, out identity) && identity == currentIdentity
                && guard.TailMatches(buffer.Word + buffer.Suffix) && currentFocus.Same(Native.Focus()) && watcher.Serial == pendingSerial;
        }
        private void ReplaceWord(string replacement, Language target, bool automatic, bool preserveLayout = false, bool custom = false, bool spelling = false)
        {
            if (!CanReplace()) { Reset(); return; }
            if (!preserveLayout && !catalog.Available(target)) { Notify("Раскладка " + Layouts.Names[(int)target] + " не установлена в Windows."); return; }
            string old = buffer.Word, suffix = buffer.Suffix; Language oldLanguage = buffer.Language;
            if (!Native.ReplaceChecked(currentFocus, old + suffix, replacement + suffix, () => watcher.Serial == pendingSerial && !Native.ModifiersDown))
            { Reset(); Notify("Windows не приняла замену. Проверьте текст: приложение может работать с правами администратора."); return; }
            undoWord = old; undoSuffix = suffix; undoLanguage = oldLanguage; undoAvailable = true;
            buffer.Word = replacement; buffer.Language = target;
            if (!preserveLayout) catalog.Switch(currentFocus, target);
            stateLabel.Text = (custom ? "Своя замена" : spelling ? "Орфография" : automatic ? "Автоисправление" : "Замена") + " → " + Layouts.Names[(int)target]
                + (settings.Hotkeys[4].Key == 0 ? ". Горячая клавиша отмены отключена." : ". " + settings.Hotkeys[4] + " — отмена.");
        }
        private void Undo()
        {
            TraceTest("undo started");
            if (!undoAvailable || !CanReplace()) { Reset(); return; }
            TraceTest("undo checked");
            if (!Native.ReplaceChecked(currentFocus, buffer.Word + buffer.Suffix, undoWord + undoSuffix, () => watcher.Serial == pendingSerial && !Native.ModifiersDown)) { Reset(); Notify("Windows не приняла отмену. Проверьте текст."); return; }
            TraceTest("undo inserted");
            // Forget only this occurrence; future typing must still use the same rule.
            Reset();
            catalog.Switch(currentFocus, undoLanguage); stateLabel.Text = "Замена отменена. При новом вводе автозамены продолжают работать.";
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
                    if (selectionBusy) return;
                    if (id == 7)
                    {
                        FocusStamp focus = Native.Focus();
                        Schedule(delegate { CycleSelection(focus); });
                    }
                    else if (id == 5) Schedule(selectedSnapshot != null ? (Action)UndoSelection : Undo);
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
            notepadFontGuard.Dispose();
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
            if (args.Contains("--spelling-test")) return SpellingIntegrationTests.Run(AppDomain.CurrentDomain.BaseDirectory);
            if (args.Contains("--selection-test")) return SelectionIntegrationTests.Run(AppDomain.CurrentDomain.BaseDirectory);
            if (args.Contains("--ui-smoke-test")) return IntegrationTests.Preview(AppDomain.CurrentDomain.BaseDirectory);
            if (args.Contains("--startup-test")) return IntegrationTests.Startup(AppDomain.CurrentDomain.BaseDirectory);
            if (args.Contains("--notepad-test")) return NotepadTests.Run(AppDomain.CurrentDomain.BaseDirectory);
            if (args.Contains("--notepad-hotkey-test")) return NotepadTests.Run(AppDomain.CurrentDomain.BaseDirectory, true);
            if (args.Contains("--notepad-system-test-protected")) return NotepadTests.Run(AppDomain.CurrentDomain.BaseDirectory, false, true, true);
            if (args.Contains("--notepad-system-test")) return NotepadTests.Run(AppDomain.CurrentDomain.BaseDirectory, false, true);
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
