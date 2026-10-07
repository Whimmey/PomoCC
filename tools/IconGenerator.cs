using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace PomoCC
{
    /// <summary>
    /// 构建时生成多尺寸 ICO（16/24/32/48/64/128/256），供 csc 的 /win32icon 用。
    ///
    /// 为什么不用 Bitmap.GetHicon() + Icon.Save()：那条路径会把帧**降成 4 位色并丢掉 alpha**，
    /// 而 ICO 目录条目里仍按 32bpp 声明 —— 结果 exe 里的图标渲染出来是错色
    /// （品牌红 #E24A3D 变成纯红、绿叶 #489648 变成灰）。所以这里直接自己编码帧：
    ///   · 小尺寸（≤64）用 32bpp BMP 帧（BITMAPINFOHEADER + 自下而上 BGRA + 1bpp AND 掩码）；
    ///   · 大尺寸（≥128）用 PNG 帧（256 的 BMP 帧要 270 KB）。
    /// 写完还会**重新读回来解码自校验**（必须同时找到品牌红和叶子绿），失败就返回非 0，
    /// 让 build.ps1 直接失败，避免再发出一个图标是错色的版本。
    /// </summary>
    internal static class IconGenerator
    {
        private const int PngFromSize = 128;                       // 从这个尺寸起用 PNG 帧
        private static readonly Color BodyColor = Color.FromArgb(226, 74, 61);   // 与 IconArt 一致
        private static readonly Color LeafColor = Color.FromArgb(72, 150, 72);

        private sealed class IconImage
        {
            internal int Size;
            internal byte[] Data;
            internal bool IsPng;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            if (args == null || args.Length != 1)
            {
                Console.Error.WriteLine("usage: IconGenerator <output.ico>");
                return 2;
            }

            int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
            List<IconImage> images = new List<IconImage>();
            foreach (int size in sizes)
            {
                images.Add(RenderFrame(size));
            }

            WriteIcon(args[0], images);

            string problem;
            if (!VerifyIcon(args[0], out problem))
            {
                Console.Error.WriteLine("icon self-check failed: " + problem);
                return 3;
            }
            return 0;
        }

        // ---------------------------------------------------------------- 画 + 编码

        private static IconImage RenderFrame(int size)
        {
            using (Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                IconArt.DrawTomato(graphics, size);

                IconImage image = new IconImage();
                image.Size = size;
                if (size >= PngFromSize)
                {
                    image.IsPng = true;
                    using (MemoryStream stream = new MemoryStream())
                    {
                        bitmap.Save(stream, ImageFormat.Png);
                        image.Data = stream.ToArray();
                    }
                }
                else
                {
                    image.IsPng = false;
                    image.Data = EncodeBmpFrame(bitmap);
                }
                return image;
            }
        }

        /// <summary>32bpp BMP 帧：40 字节头 + 自下而上 BGRA 像素 + 1bpp AND 掩码（每行 4 字节对齐）。</summary>
        private static byte[] EncodeBmpFrame(Bitmap bitmap)
        {
            int size = bitmap.Width;
            int maskStride = ((size + 31) / 32) * 4;
            int pixelsBytes = size * size * 4;
            int maskBytes = maskStride * size;

            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write(40);                       // biSize
                writer.Write(size);                     // biWidth
                writer.Write(size * 2);                 // biHeight = 颜色 + 掩码
                writer.Write((ushort)1);                // biPlanes
                writer.Write((ushort)32);               // biBitCount ← 与 ICO 目录条目一致
                writer.Write(0);                        // biCompression = BI_RGB
                writer.Write(pixelsBytes + maskBytes);  // biSizeImage
                writer.Write(0);                        // biXPelsPerMeter
                writer.Write(0);                        // biYPelsPerMeter
                writer.Write(0);                        // biClrUsed
                writer.Write(0);                        // biClrImportant

                for (int y = size - 1; y >= 0; y--)      // BMP 是自下而上
                {
                    for (int x = 0; x < size; x++)
                    {
                        Color c = bitmap.GetPixel(x, y);
                        writer.Write(c.B);
                        writer.Write(c.G);
                        writer.Write(c.R);
                        writer.Write(c.A);
                    }
                }

                // AND 掩码：全 0 = 不透明；真正的透明交给 alpha 通道（32bpp 的标准做法）
                byte[] emptyRow = new byte[maskStride];
                for (int y = 0; y < size; y++) writer.Write(emptyRow);

                writer.Flush();
                return stream.ToArray();
            }
        }

        private static void WriteIcon(string path, List<IconImage> images)
        {
            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0);                 // reserved
                writer.Write((ushort)1);                 // type = icon
                writer.Write((ushort)images.Count);

                int offset = 6 + images.Count * 16;
                foreach (IconImage image in images)
                {
                    byte dimension = image.Size >= 256 ? (byte)0 : (byte)image.Size;
                    writer.Write(dimension);             // width（0 = 256）
                    writer.Write(dimension);             // height
                    writer.Write((byte)0);               // 调色板颜色数
                    writer.Write((byte)0);               // reserved
                    writer.Write((ushort)1);             // planes
                    writer.Write((ushort)32);            // bit count：帧本身就是 32bpp（或 PNG）
                    writer.Write((uint)image.Data.Length);
                    writer.Write((uint)offset);
                    offset += image.Data.Length;
                }

                foreach (IconImage image in images) writer.Write(image.Data);
            }
        }

        // ---------------------------------------------------------------- 自校验

        /// <summary>把写出的 ICO 读回来解码 32x32 帧，确认品牌红与叶子绿都在。</summary>
        private static bool VerifyIcon(string path, out string problem)
        {
            problem = null;
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                int count = BitConverter.ToUInt16(bytes, 4);
                for (int i = 0; i < count; i++)
                {
                    int entry = 6 + i * 16;
                    int width = bytes[entry] == 0 ? 256 : bytes[entry];
                    if (width != 32) continue;

                    int declaredBits = BitConverter.ToUInt16(bytes, entry + 6);
                    int length = (int)BitConverter.ToUInt32(bytes, entry + 8);
                    int offset = (int)BitConverter.ToUInt32(bytes, entry + 12);
                    if (declaredBits != 32) { problem = "32x32 frame declares " + declaredBits + "bpp"; return false; }

                    int headerSize = BitConverter.ToInt32(bytes, offset);
                    int actualBits = BitConverter.ToUInt16(bytes, offset + 14);
                    if (headerSize != 40 || actualBits != 32)
                    {
                        problem = "32x32 frame header says " + actualBits + "bpp (header " + headerSize + ")";
                        return false;
                    }

                    int reds = 0, greens = 0;
                    for (int y = 0; y < 32; y++)
                    {
                        for (int x = 0; x < 32; x++)
                        {
                            int p = offset + 40 + ((y * 32) + x) * 4;
                            if (p + 3 >= offset + length) break;
                            int b = bytes[p], g = bytes[p + 1], r = bytes[p + 2], a = bytes[p + 3];
                            if (a < 128) continue;
                            if (Near(r, g, b, BodyColor, 24)) reds++;
                            else if (Near(r, g, b, LeafColor, 30)) greens++;
                        }
                    }

                    if (reds < 20 || greens < 6)
                    {
                        problem = "brand colors missing (red " + reds + "px, green " + greens + "px)";
                        return false;
                    }
                    return true;
                }
                problem = "no 32x32 frame found";
                return false;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return false;
            }
        }

        private static bool Near(int r, int g, int b, Color want, int tolerance)
        {
            return Math.Abs(r - want.R) <= tolerance
                && Math.Abs(g - want.G) <= tolerance
                && Math.Abs(b - want.B) <= tolerance;
        }
    }
}
