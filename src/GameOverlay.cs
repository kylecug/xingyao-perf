using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XingyaoPerf
{
    // The monitoring bars at the top-left of a game or fullscreen app while it is in front.
    // It is a separate click-through topmost window. Nothing is injected into the game, so anti-cheat
    // software has nothing to object to. True exclusive fullscreen can still draw over it; borderless
    // and Windows 11 "fullscreen optimizations" modes show it.
    internal sealed class GameOverlay : NativeWindow, IDisposable
    {
        public const int Off = 0, GamesOnly = 1, AllFullscreen = 2;
        const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
        const int Margin = 12;

        readonly Overlay renderer = new Overlay(); // used only to draw the bars; it never creates a window
        readonly Dictionary<uint, bool> isGame = new Dictionary<uint, bool>();
        string[] games = new string[0];
        Snapshot values;
        Font font;
        int lastH, lastX = int.MinValue, lastY = int.MinValue;
        bool shown, dirty = true;

        // Process names without ".exe", compared case-insensitively. A game shows the bars even in a window.
        public void SetGames(string[] names) { games = names; isGame.Clear(); }

        public void SetValues(Snapshot s) { values = s; dirty = true; }

        bool IsGame(IntPtr w)
        {
            uint pid;
            Native.GetWindowThreadProcessId(w, out pid);
            if (pid == 0) return false;
            bool hit;
            if (isGame.TryGetValue(pid, out hit)) return hit;
            string name = "";
            // The name comes from the system process list, so it also works for processes protected by anti-cheat.
            try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName; } catch { }
            hit = false;
            foreach (var g in games) if (string.Equals(g.Trim(), name, StringComparison.OrdinalIgnoreCase)) hit = true;
            if (isGame.Count > 200) isGame.Clear();
            isGame[pid] = hit;
            return hit;
        }

        public void Hide()
        {
            if (shown && Handle != IntPtr.Zero) Native.ShowWindow(Handle, Native.SW_HIDE);
            shown = false;
        }

        // Called on the UI timer.
        public void Track(int when)
        {
            IntPtr f = Native.GetForegroundWindow();
            Native.RECT r;
            bool want = when != Off && values != null && f != IntPtr.Zero && !Native.IsIconic(f) && Native.GetWindowRect(f, out r)
                && (IsGame(f) || when == AllFullscreen && Overlay.IsFullscreen(f));
            if (!want) { Hide(); return; }
            Native.GetWindowRect(f, out r);

            // Size from the screen height: 48 px bars at 1080p, about the taskbar bars at 150% scaling.
            int h = Math.Max(48, Screen.FromHandle(f).Bounds.Height / 30);
            if (h != lastH)
            {
                if (font != null) font.Dispose();
                font = Overlay.MakeFont(h);
                lastH = h; dirty = true;
            }
            if (Handle == IntPtr.Zero)
            {
                var c = new CreateParams { Caption = "xingyao-perf game", Style = Native.WS_POPUP, Width = 1, Height = 1 };
                c.ExStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
                CreateHandle(c);
                dirty = true;
            }
            int x = r.Left + Margin, y = r.Top + Margin;
            if (x != lastX || y != lastY) { lastX = x; lastY = y; dirty = true; }
            if (dirty) { Draw(); dirty = false; }
            if (!shown) { Native.ShowWindow(Handle, Native.SW_SHOWNA); shown = true; }
            // Games are often topmost too, so take the top of the band again each tick.
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        void Draw()
        {
            int pad = lastH / 8;
            int barW = Overlay.WidthFor(lastH, false), w = barW + pad * 2, h = lastH + pad;
            renderer.SetValues(values);
            using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var bg = new SolidBrush(Color.FromArgb(150, 10, 10, 12)))
                    using (var path = new GraphicsPath())
                    {
                        float d = pad * 2;
                        path.AddArc(0, 0, d, d, 180, 90);
                        path.AddArc(w - d - 1, 0, d, d, 270, 90);
                        path.AddArc(w - d - 1, h - d - 1, d, d, 0, 90);
                        path.AddArc(0, h - d - 1, d, d, 90, 90);
                        path.CloseFigure();
                        g.FillPath(bg, path);
                    }
                    using (var bars = renderer.Render(barW, lastH, font, false, false)) g.DrawImage(bars, pad, pad / 2, barW, lastH);
                }
                IntPtr screen = Native.GetDC(IntPtr.Zero), mem = Native.CreateCompatibleDC(screen);
                IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = Native.SelectObject(mem, hbmp);
                try
                {
                    var pos = new Native.POINT(lastX, lastY);
                    var size = new Native.SIZE(w, h);
                    var src = new Native.POINT(0, 0);
                    var blend = new Native.BLENDFUNCTION { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
                    Native.UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2);
                }
                finally
                {
                    Native.SelectObject(mem, old);
                    Native.DeleteObject(hbmp);
                    Native.DeleteDC(mem);
                    Native.ReleaseDC(IntPtr.Zero, screen);
                }
            }
        }

        public void Dispose()
        {
            if (font != null) font.Dispose();
            renderer.Dispose();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
