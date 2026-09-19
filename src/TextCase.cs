using System;
using System.Text;
using System.Text.RegularExpressions;

namespace TriSwitch
{
    internal enum TextCaseMode { Title, Upper, Lower, Sentence }

    internal static class TextCase
    {
        internal static string Convert(string text, TextCaseMode mode)
        {
            if (mode == TextCaseMode.Upper) return text.ToUpperInvariant();
            string lower = text.ToLowerInvariant();
            if (mode == TextCaseMode.Lower) return lower;
            if (mode == TextCaseMode.Title)
                return Regex.Replace(lower, @"\p{L}[\p{L}\p{M}]*(?:['’ʼ][\p{L}\p{M}]+)*", m => char.ToUpperInvariant(m.Value[0]) + m.Value.Substring(1));
            var result = new StringBuilder(lower); bool beginning = true;
            for (int i = 0; i < lower.Length; i++)
            {
                char c = lower[i];
                if (char.IsLetter(c))
                {
                    if (beginning) result[i] = char.ToUpperInvariant(c);
                    beginning = false;
                }
                else if (c == '!' || c == '?' || c == '…' || c == '\n' || c == '\r'
                    || (c == '.' && !(i > 0 && i + 1 < lower.Length && char.IsDigit(lower[i - 1]) && char.IsDigit(lower[i + 1])))) beginning = true;
            }
            return result.ToString();
        }
    }
}
