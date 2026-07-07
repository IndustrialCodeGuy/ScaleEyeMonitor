namespace Scale_Eye_Monitor
{
    internal sealed class DpiMetrics
    {
        public const int BaseDpi = 96;

        public DpiMetrics(int dpi)
        {
            Dpi = dpi > 0 ? dpi : BaseDpi;
            ScaleFactor = Dpi / (float)BaseDpi;
        }

        public int Dpi { get; }
        public float ScaleFactor { get; }

        public int Scale(int value)
        {
            if (value == 0) return 0;

            int scaled = (int)Math.Round(value * ScaleFactor, MidpointRounding.AwayFromZero);
            return value > 0 ? Math.Max(1, scaled) : Math.Min(-1, scaled);
        }

        public Size Size(int width, int height) => new(Scale(width), Scale(height));

        public Padding Padding(int all) => new(Scale(all));
        public Padding Padding(int left, int top, int right, int bottom) =>
            new(Scale(left), Scale(top), Scale(right), Scale(bottom));

        public int TextRowHeight(Font font, int verticalPadding = 6)
        {
            int textHeight = TextRenderer.MeasureText(
                "Hg",
                font,
                System.Drawing.Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Height;

            return Math.Max(Scale(20), textHeight + Scale(verticalPadding));
        }

        public int FieldHeight(Font font) => Math.Max(Scale(23), TextRowHeight(font, 8));
        public int ButtonHeight(Font font) => Math.Max(Scale(28), TextRowHeight(font, 10));
    }

    internal static class DpiLayout
    {
        public static DpiMetrics For(Control control) => new(GetDpi(control));

        private static int GetDpi(Control control)
        {
            try
            {
                int dpi = control.DeviceDpi;
                if (dpi > 0) return dpi;
            }
            catch
            {
                // Fall back below for controls without a created handle.
            }

            try
            {
                using var g = control.CreateGraphics();
                int dpi = (int)Math.Round(g.DpiX, MidpointRounding.AwayFromZero);
                if (dpi > 0) return dpi;
            }
            catch
            {
                // Use 96 DPI if no graphics context is available yet.
            }

            return DpiMetrics.BaseDpi;
        }
    }
}
