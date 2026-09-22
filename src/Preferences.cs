using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TriSwitch
{
    [DataContract]
    public sealed class HotkeyBinding
    {
        [DataMember] public uint Key;
        [DataMember] public uint Modifiers;
        public HotkeyBinding Copy() { return new HotkeyBinding { Key = Key, Modifiers = Modifiers }; }
        public bool Same(HotkeyBinding other) { return other != null && Key == other.Key && Modifiers == other.Modifiers; }
        public override string ToString()
        {
            if (Key == 0) return "Отключено";
            string name = Key >= 0x30 && Key <= 0x39 ? ((char)Key).ToString() : ((Keys)Key).ToString();
            if (Key == 8) name = "Backspace";
            if (Key == 32) name = "Space";
            if (Key == 145) name = "Scroll Lock";
            return ((Modifiers & 2) != 0 ? "Ctrl + " : "") + ((Modifiers & 1) != 0 ? "Alt + " : "") + ((Modifiers & 4) != 0 ? "Shift + " : "") + name;
        }
        public static readonly string[] Actions = { "Слово → английский", "Слово → русский", "Слово → украинский", "Перебрать раскладки", "Отменить замену", "Пауза / продолжение" };
        public static HotkeyBinding[] Defaults()
        { return new uint[] { 0x31, 0x32, 0x33, 0x75, 8, 32 }.Select(k => new HotkeyBinding { Key = k, Modifiers = 3 }).ToArray(); }
        public static void Validate(HotkeyBinding[] bindings)
        {
            if (bindings == null || bindings.Length != 6 || bindings.Any(b => b == null)) throw new ArgumentException("Нужно настроить все шесть действий.");
            var used = new HashSet<string>();
            for (int i = 0; i < bindings.Length; i++)
            {
                HotkeyBinding b = bindings[i];
                if (b.Key == 0 && b.Modifiers == 0) continue;
                bool function = b.Key >= 112 && b.Key <= 135 && b.Key != 123;
                bool keyAllowed = function || (b.Key >= 48 && b.Key <= 57) || (b.Key >= 65 && b.Key <= 90) || (b.Key >= 96 && b.Key <= 111)
                    || new uint[] { 8, 9, 13, 19, 27, 32, 33, 34, 35, 36, 37, 38, 39, 40, 45, 46, 145, 186, 187, 188, 189, 190, 191, 192, 219, 220, 221, 222, 226 }.Contains(b.Key);
                bool singleAllowed = function || b.Key == 19 || b.Key == 145;
                if (!keyAllowed || (b.Modifiers & ~7u) != 0 || ((b.Modifiers & 3) == 0 && !singleAllowed)
                    || (b.Key == 46 && (b.Modifiers & 3) == 3) || (b.Key == 9 && b.Modifiers == 1))
                    throw new ArgumentException(Actions[i] + ": используйте Ctrl/Alt с клавишей либо F1–F11, F13–F24, Pause, Scroll Lock. F12, Win и системные сочетания недоступны.");
                if (!used.Add(b.Key + ":" + b.Modifiers)) throw new ArgumentException("Сочетание «" + b + "» назначено нескольким действиям.");
            }
        }
    }

    [DataContract]
    public sealed class ReplacementRule
    {
        [DataMember] public bool Enabled = true;
        [DataMember] public string From = "";
        [DataMember] public string To = "";
        // -1 preserves the active layout; 0, 1, 2 correspond to EN, RU, UK.
        [DataMember] public int Target = -1;
        [DataMember] public bool PreserveCase;
        public ReplacementRule Copy() { return new ReplacementRule { Enabled = Enabled, From = From, To = To, Target = Target, PreserveCase = PreserveCase }; }
    }

    public sealed class ReplacementBook
    {
        private readonly Dictionary<string, ReplacementRule> rules = new Dictionary<string, ReplacementRule>(StringComparer.OrdinalIgnoreCase);
        public ReplacementBook(IEnumerable<ReplacementRule> values)
        {
            int row = 0;
            foreach (ReplacementRule raw in values)
            {
                row++;
                if (raw == null || string.IsNullOrWhiteSpace(raw.From) || raw.From.Length > 64 || raw.From.Any(char.IsWhiteSpace)
                    || string.IsNullOrWhiteSpace(raw.To) || raw.To.Length > 512 || raw.Target < -1 || raw.Target > 2)
                    throw new ArgumentException("Замена " + row + ": укажите слово без пробелов (до 64 символов), результат (до 512 символов) и раскладку.");
                if ((raw.From + raw.To).Any(c => char.IsControl(c) || char.IsSurrogate(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark
                    || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.SpacingCombiningMark || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.EnclosingMark))
                    throw new ArgumentException("Замена " + row + ": в этой версии поддерживается одна строка текста без эмодзи и составных символов.");
                if (raw.From == raw.To) throw new ArgumentException("Замена " + row + ": исходное слово и результат совпадают.");
                if (!raw.Enabled) continue;
                if (rules.ContainsKey(raw.From)) throw new ArgumentException("Для «" + raw.From + "» включено несколько замен. Оставьте одну.");
                rules.Add(raw.From, raw.Copy());
            }
        }
        public Suggestion Find(string word, Language source, ISet<string> ignored)
        {
            string bare = word.TrimEnd('.', ',', '!', '?', ':', ';', ')', '"', '»');
            if (ignored.Contains(word) || ignored.Contains(bare)) return null;
            ReplacementRule rule; string suffix = "";
            if (!rules.TryGetValue(word, out rule))
            {
                if (bare == word || !rules.TryGetValue(bare, out rule)) return null;
                suffix = word.Substring(bare.Length);
            }
            return new Suggestion { Text = (rule.PreserveCase ? MatchCase(word.Substring(0, word.Length - suffix.Length), rule.To) : rule.To) + suffix, Language = rule.Target < 0 ? source : (Language)rule.Target, PreserveLayout = rule.Target < 0, Custom = true };
        }

        private static string MatchCase(string input, string replacement)
        {
            char[] letters = input.Where(c => char.IsUpper(c) || char.IsLower(c)).ToArray();
            if (letters.Length == 0) return replacement;
            if (letters.All(char.IsLower)) return replacement.ToLowerInvariant();
            if (letters.All(char.IsUpper)) return replacement.ToUpperInvariant();
            if (char.IsUpper(letters[0]) && letters.Skip(1).All(char.IsLower))
            {
                char[] result = replacement.ToLowerInvariant().ToCharArray();
                for (int i = 0; i < result.Length; i++)
                    if (char.IsLetter(result[i])) { result[i] = char.ToUpperInvariant(result[i]); break; }
                return new string(result);
            }
            return replacement; // Mixed case has no unambiguous correspondence for phrases.
        }
    }

    [DataContract]
    public sealed class Settings
    {
        [DataMember] public bool Automatic = true;
        [DataMember] public bool SpellCheck = true;
        [DataMember] public Language CyrillicPriority = Language.Russian;
        [DataMember] public string Exclusions = "Code\r\ndevenv\r\nWindowsTerminal\r\npowershell\r\npwsh\r\ncmd\r\nconhost\r\nmstsc\r\nCredentialUIBroker\r\nKeePass\r\nKeePassXC\r\n1Password\r\nBitwarden";
        [DataMember] public string IgnoreWords = "";
        [DataMember] public HotkeyBinding[] Hotkeys = HotkeyBinding.Defaults();
        [DataMember] public List<ReplacementRule> Replacements = new List<ReplacementRule>();
        [OnDeserializing] private void Initialize(StreamingContext context)
        { var defaults = new Settings(); Automatic = defaults.Automatic; SpellCheck = defaults.SpellCheck; CyrillicPriority = defaults.CyrillicPriority; Exclusions = defaults.Exclusions; IgnoreWords = ""; Hotkeys = defaults.Hotkeys; Replacements = defaults.Replacements; }
        public Settings Copy()
        { return new Settings { Automatic = Automatic, SpellCheck = SpellCheck, CyrillicPriority = CyrillicPriority, Exclusions = Exclusions, IgnoreWords = IgnoreWords, Hotkeys = Hotkeys.Select(b => b.Copy()).ToArray(), Replacements = Replacements.Select(r => r.Copy()).ToList() }; }
        private void ValidateCyrillicPriority()
        {
            if (CyrillicPriority != Language.Russian && CyrillicPriority != Language.Ukrainian)
                throw new ArgumentException("Приоритет кириллицы: выберите русский или украинский язык.");
        }
        public static Settings Load(string path)
        {
            if (!File.Exists(path)) return new Settings();
            // Read through StreamReader so user-edited UTF-8 files with a BOM
            // remain compatible with the .NET Framework JSON serializer.
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(File.ReadAllText(path, Encoding.UTF8))))
            {
                var value = (Settings)new DataContractJsonSerializer(typeof(Settings)).ReadObject(stream);
                if (value == null) throw new InvalidDataException("Файл настроек пуст.");
                value.Exclusions = value.Exclusions ?? ""; value.IgnoreWords = value.IgnoreWords ?? "";
                value.Hotkeys = value.Hotkeys ?? HotkeyBinding.Defaults(); value.Replacements = value.Replacements ?? new List<ReplacementRule>();
                value.ValidateCyrillicPriority(); HotkeyBinding.Validate(value.Hotkeys); new ReplacementBook(value.Replacements);
                return value;
            }
        }
        public void Save(string path)
        {
            ValidateCyrillicPriority(); HotkeyBinding.Validate(Hotkeys); new ReplacementBook(Replacements);
            string temp = path + ".tmp";
            try
            {
                using (var stream = File.Create(temp)) new DataContractJsonSerializer(typeof(Settings)).WriteObject(stream, this);
                for (int attempt = 0; ; attempt++)
                {
                    try { if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path); break; }
                    catch (IOException) { if (attempt >= 2 || !File.Exists(temp)) throw; Thread.Sleep(40 * (attempt + 1)); }
                }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    // Stage only new key combinations. Existing ones remain registered until
    // every new combination and the settings file have been accepted.
    internal sealed class HotkeyRegistry : IDisposable
    {
        private sealed class Registration { public int Id, Action; public HotkeyBinding Binding; }
        private List<Registration> active = new List<Registration>();
        private int nextId = 100;
        private readonly Func<int, HotkeyBinding, bool> register;
        private readonly Action<int> unregister;
        public HotkeyRegistry(Func<int, HotkeyBinding, bool> register, Action<int> unregister)
        { this.register = register; this.unregister = unregister; }
        private Registration Create(HotkeyBinding binding, int action)
        {
            nextId = nextId >= 0xBFFF ? 100 : nextId + 1;
            while (active.Any(a => a.Id == nextId)) nextId++;
            var value = new Registration { Id = nextId, Action = action, Binding = binding.Copy() };
            return register(value.Id, value.Binding) ? value : null;
        }
        public List<string> Start(HotkeyBinding[] bindings)
        {
            HotkeyBinding.Validate(bindings); var failures = new List<string>();
            for (int i = 0; i < bindings.Length; i++)
            {
                if (bindings[i].Key == 0) continue;
                Registration value = Create(bindings[i], i + 1);
                if (value != null) active.Add(value); else failures.Add(HotkeyBinding.Actions[i] + ": " + bindings[i]);
            }
            return failures;
        }
        public bool Apply(HotkeyBinding[] bindings, Action persist, out string error)
        {
            var staged = new List<Registration>(); var added = new List<Registration>(); error = null;
            try
            {
                HotkeyBinding.Validate(bindings);
                for (int i = 0; i < bindings.Length; i++)
                {
                    if (bindings[i].Key == 0) continue;
                    Registration previous = active.FirstOrDefault(a => a.Binding.Same(bindings[i]));
                    Registration value;
                    if (previous != null) value = new Registration { Id = previous.Id, Action = i + 1, Binding = bindings[i].Copy() };
                    else
                    {
                        value = Create(bindings[i], i + 1);
                        if (value == null) throw new InvalidOperationException("Сочетание «" + bindings[i] + "» занято или недоступно в Windows. Прежние назначения сохранены.");
                        added.Add(value);
                    }
                    staged.Add(value);
                }
                persist();
            }
            catch (Exception e) { foreach (Registration a in added) unregister(a.Id); error = e.Message; return false; }
            foreach (Registration old in active.Where(a => !staged.Any(s => s.Id == a.Id))) unregister(old.Id);
            active = staged; return true;
        }
        public bool Matches(KeyEvent e)
        {
            uint modifiers = (e.Alt ? 1u : 0u) | (e.Ctrl ? 2u : 0u) | (e.Shift ? 4u : 0u) | (e.Win ? 8u : 0u);
            return active.Any(a => a.Binding.Key == e.Vk && a.Binding.Modifiers == modifiers);
        }
        public int Resolve(int nativeId, uint key, uint modifiers)
        { Registration found = active.FirstOrDefault(a => a.Id == nativeId && a.Binding.Key == key && a.Binding.Modifiers == modifiers); return found == null ? 0 : found.Action; }
        public void Dispose() { foreach (Registration a in active) unregister(a.Id); active.Clear(); }
    }

    internal sealed class HotkeyBox : TextBox
    {
        private HotkeyBinding binding;
        public HotkeyBinding Binding { get { return binding.Copy(); } set { binding = value.Copy(); Text = binding.ToString(); } }
        public HotkeyBox(HotkeyBinding value)
        { ReadOnly = true; ShortcutsEnabled = false; Dock = DockStyle.Fill; BackColor = System.Drawing.Color.White; Binding = value; }
        public void CaptureGesture(uint key, uint modifiers)
        {
            if (key == 16 || key == 17 || key == 18 || key == 91 || key == 92 || (key >= 160 && key <= 165)) return;
            Binding = (modifiers == 0 && (key == 8 || key == 46)) ? new HotkeyBinding() : new HotkeyBinding { Key = key, Modifiers = modifiers };
            SelectAll();
        }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.Tab && (keyData & (Keys.Control | Keys.Alt)) == 0) return base.ProcessCmdKey(ref message, keyData);
            uint modifiers = ((keyData & Keys.Control) != 0 ? 2u : 0u) | ((keyData & Keys.Alt) != 0 ? 1u : 0u) | ((keyData & Keys.Shift) != 0 ? 4u : 0u);
            CaptureGesture((uint)key, modifiers); return true;
        }
    }
}
