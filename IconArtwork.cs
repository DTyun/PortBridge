using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace PortBridge {
    public enum IconState { App, Starting, Running, Paused, Success, Failure }

    public static class IconArtwork {
        public static Bitmap CreateBitmap(IconState state, int size) {
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap)) {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                float s = size / 64f;
                g.ScaleTransform(s, s);
                Color blue = Color.FromArgb(51, 102, 204);
                Color bg = state == IconState.Success || state == IconState.Running ? Color.FromArgb(8, 127, 91)
                    : state == IconState.Failure ? Color.FromArgb(194, 65, 59)
                    : state == IconState.Paused ? Color.FromArgb(100, 116, 139)
                    : state == IconState.Starting ? Color.FromArgb(217, 140, 34) : blue;
                using (var path = Rounded(4, 4, 56, 56, 15))
                using (var fill = new SolidBrush(bg)) g.FillPath(fill, path);

                using (var pen = new Pen(Color.White, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) {
                    g.DrawLine(pen, 14, 23, 49, 23);
                    g.DrawLine(pen, 42, 16, 49, 23);
                    g.DrawLine(pen, 49, 23, 42, 30);
                    g.DrawLine(pen, 50, 41, 15, 41);
                    g.DrawLine(pen, 22, 34, 15, 41);
                    g.DrawLine(pen, 15, 41, 22, 48);
                }

                if (state != IconState.App) DrawBadge(g, state);
            }
            return bitmap;
        }

        static GraphicsPath Rounded(float x, float y, float w, float h, float r) {
            var path = new GraphicsPath();
            path.AddArc(x, y, r, r, 180, 90); path.AddArc(x + w - r, y, r, r, 270, 90);
            path.AddArc(x + w - r, y + h - r, r, r, 0, 90); path.AddArc(x, y + h - r, r, r, 90, 90);
            path.CloseFigure(); return path;
        }

        static void DrawBadge(Graphics g, IconState state) {
            Color color = state == IconState.Failure ? Color.FromArgb(194, 65, 59)
                : state == IconState.Success || state == IconState.Running ? Color.FromArgb(8, 127, 91)
                : state == IconState.Paused ? Color.FromArgb(100, 116, 139) : Color.FromArgb(217, 140, 34);
            using (var brush = new SolidBrush(color)) g.FillEllipse(brush, 38, 38, 24, 24);
            using (var pen = new Pen(Color.White, 2.7f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }) {
                if (state == IconState.Success) {
                    g.DrawLine(pen, 44, 50, 48, 54); g.DrawLine(pen, 48, 54, 56, 45);
                } else if (state == IconState.Failure) {
                    g.DrawLine(pen, 46, 46, 54, 54); g.DrawLine(pen, 54, 46, 46, 54);
                } else if (state == IconState.Paused) {
                    g.FillRectangle(Brushes.White, 45, 45, 3, 10); g.FillRectangle(Brushes.White, 51, 45, 3, 10);
                } else if (state == IconState.Starting) {
                    using (var arc = new Pen(Color.White, 2.8f)) g.DrawArc(arc, 44, 44, 12, 12, 35, 270);
                    g.FillEllipse(Brushes.White, 52, 43, 3, 3);
                } else if (state == IconState.Running) {
                    g.DrawLine(pen, 46, 51, 46, 53); g.DrawLine(pen, 50, 47, 50, 54); g.DrawLine(pen, 54, 49, 54, 52);
                } else {
                    g.DrawLine(pen, 45, 50, 49, 54); g.DrawLine(pen, 49, 54, 56, 46);
                }
            }
        }

        public static Icon CreateIcon(IconState state, int size) {
            using (var bitmap = CreateBitmap(state, size)) {
                IntPtr handle = bitmap.GetHicon();
                try { return (Icon)Icon.FromHandle(handle).Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        public static void WriteIconSet(string directory) {
            Directory.CreateDirectory(directory);
            SaveIco(Path.Combine(directory, "PortBridge.ico"), IconState.App);
            SaveIco(Path.Combine(directory, "starting.ico"), IconState.Starting);
            SaveIco(Path.Combine(directory, "running.ico"), IconState.Running);
            SaveIco(Path.Combine(directory, "paused.ico"), IconState.Paused);
            SaveIco(Path.Combine(directory, "connected.ico"), IconState.Success);
            SaveIco(Path.Combine(directory, "failed.ico"), IconState.Failure);
        }

        static void SaveIco(string path, IconState state) {
            int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
            using (var output = File.Create(path)) using (var writer = new BinaryWriter(output)) {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
                var frames = new byte[sizes.Length][];
                for (int i = 0; i < sizes.Length; i++) {
                    using (var bitmap = CreateBitmap(state, sizes[i])) using (var memory = new MemoryStream()) {
                        bitmap.Save(memory, ImageFormat.Png); frames[i] = memory.ToArray();
                    }
                }
                int offset = 6 + (16 * sizes.Length);
                for (int i = 0; i < sizes.Length; i++) {
                    int dimension = sizes[i] == 256 ? 0 : sizes[i];
                    writer.Write((byte)dimension); writer.Write((byte)dimension); writer.Write((byte)0); writer.Write((byte)0);
                    writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(frames[i].Length); writer.Write(offset);
                    offset += frames[i].Length;
                }
                for (int i = 0; i < frames.Length; i++) writer.Write(frames[i]);
            }
        }

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);
    }
}
