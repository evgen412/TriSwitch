using System.Drawing;
using System.Windows.Forms;

namespace TriSwitch
{
    internal static class Theme
    {
        public static readonly Color Accent = Color.FromArgb(64, 112, 208);
        public static readonly Color AccentBorder = Color.FromArgb(51, 91, 172);
        public static readonly Color Heading = Color.FromArgb(49, 77, 125);
        public static readonly Color MutedText = Color.FromArgb(81, 104, 141);
        public static readonly Color TabBackground = Color.FromArgb(226, 234, 246);
        public static readonly Color TabBorder = Color.FromArgb(127, 149, 182);

        public static void HighlightButton(Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            button.BackColor = TabBackground;
            button.ForeColor = Heading;
            button.FlatAppearance.BorderColor = AccentBorder;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(204, 222, 250);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(178, 204, 242);
        }
    }
}
