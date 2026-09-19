using System;
using System.Drawing;
using System.Windows.Forms;

namespace TriSwitch
{
    internal sealed class ContrastTabControl : TabControl
    {
        public ContrastTabControl()
        {
            DrawMode = TabDrawMode.OwnerDrawFixed;
            SizeMode = TabSizeMode.Fixed;
            Multiline = true;
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateHeaderSize(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdateHeaderSize(); }
        protected override void OnControlAdded(ControlEventArgs e) { base.OnControlAdded(e); UpdateHeaderSize(); }
        private void UpdateHeaderSize()
        {
            if (!IsHandleCreated || TabCount == 0) return;
            using (Graphics graphics = CreateGraphics())
            using (var selectedFont = new Font(Font, FontStyle.Bold))
            {
                float scale = graphics.DpiX / 96f;
                int width = 0, height = 0;
                foreach (TabPage page in TabPages)
                {
                    Size text = TextRenderer.MeasureText(graphics, page.Text, selectedFont, Size.Empty, TextFormatFlags.NoPrefix);
                    width = Math.Max(width, text.Width); height = Math.Max(height, text.Height);
                }
                ItemSize = new Size(width + (int)(20 * scale), height + (int)(18 * scale));
            }
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= TabCount) return;
            float scale = e.Graphics.DpiX / 96f;
            bool selected = e.Index == SelectedIndex;
            Color background = selected ? Theme.Accent : Theme.TabBackground;
            Color foreground = selected ? Color.White : Theme.Heading;
            Rectangle card = e.Bounds; card.Inflate(-(int)Math.Ceiling(3 * scale), -(int)Math.Ceiling(2 * scale));
            using (var gutter = new SolidBrush(Parent == null ? SystemColors.Control : Parent.BackColor)) e.Graphics.FillRectangle(gutter, e.Bounds);
            using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, card);
            using (var border = new Pen(selected ? Theme.AccentBorder : Theme.TabBorder, Math.Max(1, scale)))
                e.Graphics.DrawRectangle(border, card.X, card.Y, card.Width - 1, card.Height - 1);
            Rectangle textBounds = card; textBounds.Inflate(-(int)(6 * scale), 0);
            using (var textFont = new Font(Font, selected ? FontStyle.Bold : FontStyle.Regular))
                TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text, textFont, textBounds, foreground,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            if (selected && Focused && ShowFocusCues)
            {
                Rectangle focus = card; focus.Inflate(-(int)(4 * scale), -(int)(4 * scale));
                ControlPaint.DrawFocusRectangle(e.Graphics, focus, foreground, background);
            }
        }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }
}
