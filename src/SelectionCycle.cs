using System;
using System.Globalization;
using System.Linq;
using System.Windows.Automation;
using System.Windows.Automation.Text;

namespace TriSwitch
{
    internal sealed class SelectionCycle
    {
        internal const int MaximumLength = 512;
        internal readonly string Original;
        internal readonly Language Source;
        internal Language Current;
        internal SelectionCycle(string text, Language source) { Original = text; Source = Current = source; }
        internal string TextFor(Language target, Func<string, Language, Language, string> convert)
        { return target == Source ? Original : convert(Original, Source, target); }
        internal static Language Next(Language current, Func<Language, bool> available)
        {
            // EN -> UK -> RU -> EN; missing Windows layouts are skipped.
            for (int step = 1; step <= 3; step++)
            {
                Language candidate = (Language)(((int)current + 3 - step) % 3);
                if (available(candidate)) return candidate;
            }
            return current;
        }
        internal static bool Supported(string text)
        {
            return !string.IsNullOrEmpty(text) && text.Length <= MaximumLength
                && text.All(c => !char.IsControl(c) && !char.IsSurrogate(c)
                    && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark
                    && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.SpacingCombiningMark
                    && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.EnclosingMark
                    && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format
                    && c != '\u2028' && c != '\u2029'
                    && (!char.IsLetter(c) || IsLatin(c) || IsCyrillic(c)));
        }
        private static bool IsLatin(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); }
        private static bool IsCyrillic(char c) { return "абвгґдеєёжзиіїйклмнопрстуфхцчшщъыьэюя".IndexOf(char.ToLowerInvariant(c)) >= 0; }
        internal static Language? DetectSource(string text, Language? active, Language preferred)
        {
            bool latin = text.Any(IsLatin), cyrillic = text.Any(IsCyrillic);
            bool russian = text.Any(c => "ёыэъЁЫЭЪ".IndexOf(c) >= 0);
            bool ukrainian = text.Any(c => "іїєґІЇЄҐ".IndexOf(c) >= 0);
            if ((latin && cyrillic) || (russian && ukrainian)) return null;
            if (latin) return Language.English;
            if (russian) return Language.Russian;
            if (ukrainian) return Language.Ukrainian;
            if (cyrillic) return active == Language.Russian || active == Language.Ukrainian ? active : preferred;
            return active;
        }
    }

    internal sealed class SelectionSnapshot
    {
        internal FocusStamp Focus;
        internal string Identity, Text;
        internal TextPatternRange Range;
        internal int Start = -1, End = -1;
        internal bool NativeEditor;

        internal static SelectionSnapshot Capture(FocusStamp focus, FocusGuard guard)
        {
            string identity;
            if (!guard.TryCheck(focus, out identity)) return null;
            try
            {
                AutomationElement element = AutomationElement.FocusedElement; object pattern;
                if (element == null || !element.TryGetCurrentPattern(TextPattern.Pattern, out pattern)) return null;
                TextPatternRange[] ranges = ((TextPattern)pattern).GetSelection();
                if (ranges.Length != 1) return null;
                object readOnly = ranges[0].GetAttributeValue(TextPattern.IsReadOnlyAttribute);
                if (readOnly is bool && (bool)readOnly) return null;
                string text = ranges[0].GetText(SelectionCycle.MaximumLength + 1);
                if (!SelectionCycle.Supported(text)) return null;
                var snapshot = new SelectionSnapshot { Focus = focus, Identity = identity, Text = text, Range = ranges[0].Clone(), NativeEditor = Native.IsNativeEditor(focus.Control) };
                if (snapshot.NativeEditor && (!Native.ReadSelection(focus.Control, out snapshot.Start, out snapshot.End)
                    || snapshot.End - snapshot.Start != text.Length)) return null;
                string checkedIdentity;
                return guard.TryCheck(focus, out checkedIdentity) && identity == checkedIdentity ? snapshot : null;
            }
            catch (Exception) { return null; }
        }
        internal bool Same(SelectionSnapshot other)
        {
            if (other == null || !Focus.Same(other.Focus) || Identity != other.Identity || Text != other.Text) return false;
            if (NativeEditor) return other.NativeEditor && Start == other.Start && End == other.End;
            try
            {
                return Range.CompareEndpoints(TextPatternRangeEndpoint.Start, other.Range, TextPatternRangeEndpoint.Start) == 0
                    && Range.CompareEndpoints(TextPatternRangeEndpoint.End, other.Range, TextPatternRangeEndpoint.End) == 0;
            }
            catch (Exception) { return false; }
        }
    }
}
