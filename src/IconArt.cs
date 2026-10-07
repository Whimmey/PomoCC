using System.Drawing;

namespace PomoCC
{
    internal static class IconArt
    {
        internal static void DrawTomato(Graphics graphics, int size)
        {
            float scale = size / 32f;
            using (SolidBrush body = new SolidBrush(Color.FromArgb(226, 74, 61)))
            {
                graphics.FillEllipse(body, 4 * scale, 9 * scale, 24 * scale, 20 * scale);
            }
            using (SolidBrush leaf = new SolidBrush(Color.FromArgb(72, 150, 72)))
            {
                graphics.FillEllipse(leaf, 12 * scale, 3 * scale, 9 * scale, 7 * scale);
                graphics.FillEllipse(leaf, 8 * scale, 6 * scale, 7 * scale, 5 * scale);
                graphics.FillEllipse(leaf, 18 * scale, 6 * scale, 7 * scale, 5 * scale);
            }
        }
    }
}
