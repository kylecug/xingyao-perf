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
        HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTime knownAt = DateTime.MinValue;

        // Windows records the games it recognises (the ones Game Bar and Game Mode act on) under this key.
        // Their launchers and clients are recognised too, so these are left out.
        const string GameStore = @"System\GameConfigStore\Children";
        static readonly string[] NotGames = { "LeagueClientUx", "LeagueClient", "RiotClientServices", "RiotClientUx", "EpicGamesLauncher", "steam", "steamwebhelper", "Battle.net", "EADesktop", "Origin", "upc", "GalaxyClient" };

        void LoadKnownGames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(GameStore))
                    if (root != null)
                        foreach (var sub in root.GetSubKeyNames())
                            using (var k = root.OpenSubKey(sub))
                            {
                                var path = k == null ? null : k.GetValue("MatchedExeFullPath") as string;
                                if (string.IsNullOrEmpty(path)) continue;
                                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                                if (Array.IndexOf(NotGames, name) < 0 && !NotGame(name)) set.Add(name);
                            }
            }
            catch (Exception e) { Log.Write("game list: " + e.Message); }
            known = set;
            knownAt = DateTime.UtcNow;
        }

        static bool NotGame(string name)
        {
            foreach (var n in NotGames) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        Snapshot values;
        Font font, fpsFont;
        readonly FrameRate fps = new FrameRate();
        double fpsValue = double.NaN;
        DateTime fpsReadAt = DateTime.UtcNow;
        int lastH, lastX = int.MinValue, lastY = int.MinValue;
        bool shown, dirty = true;

        // Extra process names without ".exe", compared case-insensitively, for games Windows does not recognise.
        // A game shows the bars even in a window.
        public void SetGames(string[] names) { games = names; isGame.Clear(); }

        public void SetValues(Snapshot s) { values = s; dirty = true; }

        bool IsGame(IntPtr w)
        {
            uint pid;
            Native.GetWindowThreadProcessId(w, out pid);
            if (pid == 0) return false;
            bool hit;
            if (isGame.TryGetValue(pid, out hit)) return hit;
            // Windows adds a game to its list the first time it runs, so the list is re-read now and then.
            if ((DateTime.UtcNow - knownAt).TotalMinutes > 5) LoadKnownGames();
            string name = "";
            // The name comes from the system process list, so it also works for processes protected by anti-cheat.
            try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName; } catch { }
            hit = name.Length > 0 && known.Contains(name);
            foreach (var g in games) if (string.Equals(g.Trim(), name, StringComparison.OrdinalIgnoreCase)) hit = true;
            if (isGame.Count > 200) isGame.Clear();
            isGame[pid] = hit;
            return hit;
        }

        // True while any process Windows recognises as a game, or listed by the user, is running. Cheap enough for every few seconds.
        public bool AnyGameRunning()
        {
            if ((DateTime.UtcNow - knownAt).TotalMinutes > 5) LoadKnownGames();
            try
            {
                foreach (var p in Process.GetProcesses())
                    using (p)
                    {
                        string name = p.ProcessName;
                        if (known.Contains(name)) return true;
                        foreach (var g in games) if (string.Equals(g.Trim(), name, StringComparison.OrdinalIgnoreCase)) return true;
                    }
            }
            catch (Exception e) { Log.Write("game scan: " + e.Message); }
            return false;
        }

        public void Hide()
        {
            if (shown &&Handle != IntPtr.Zero) Native.ShowWindow(Handle, Native.SW_HIDE);
            shown = false;
            if (fps.Running) fps.Stop(); // frames are only counted while the overlay is up
            fpsValue = double.NaN;
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
                if (fpsFont != null) fpsFont.Dispose();
                font = Overlay.MakeFont(h);
                fpsFont = new Font("Segoe UI", h * 0.4f, FontStyle.Bold, GraphicsUnit.Pixel);
                lastH = h; dirty = true;
            }

            // Frame rate of the window in front, refreshed once a second.
            uint pid;
            Native.GetWindowThreadProcessId(f, out pid);
            fps.Track((int)pid);
            if ((DateTime.UtcNow - fpsReadAt).TotalSeconds >= 1)
            {
                fpsReadAt = DateTime.UtcNow;
                double v = fps.Read();
                if (Math.Round(v) != Math.Round(fpsValue) || double.IsNaN(v) != double.IsNaN(fpsValue)) { fpsValue = v; dirty = true; }
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
            int fpsW = (int)(lastH * 1.15f); // frame rate box on the left, as tall as both bar rows
            int barW = Overlay.WidthFor(lastH, false), w = fpsW + barW + pad * 3, h = lastH + pad;
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
                    using (var bars = renderer.Render(barW, lastH, font, false, false)) g.DrawImage(bars, pad * 2 + fpsW, pad / 2, barW, lastH);
                    DrawFps(g, new RectangleF(pad, pad / 2f + lastH * 0.04f, fpsW, lastH * 0.92f));
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

        void DrawFps(Graphics g, RectangleF r)
        {
            using (var box = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
            using (var path = new GraphicsPath())
            {
                float d = r.Height / 6;
                path.AddArc(r.Left, r.Top, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(box, path);
            }
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip })
            {
                var top = new RectangleF(r.Left, r.Top, r.Width, r.Height * 0.62f);
                var bottom = new RectangleF(r.Left, r.Top + r.Height * 0.58f, r.Width, r.Height * 0.4f);
                g.DrawString(double.IsNaN(fpsValue) ? "--" : fpsValue.ToString("0"), fpsFont, Brushes.White, top, center);
                using (var dim = new SolidBrush(Color.FromArgb(200, 255, 255, 255))) g.DrawString("FPS", font, dim, bottom, center);
            }
        }

        public void Dispose()
        {
            fps.Dispose();
            if (fpsFont != null) fpsFont.Dispose();
            if (font != null) font.Dispose();
            renderer.Dispose();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
