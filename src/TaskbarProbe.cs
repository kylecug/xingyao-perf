using System;
using System.Threading;
using System.Windows.Automation;

namespace XingyaoPerf
{
    // Reads where the taskbar buttons end and where the weather (Widgets) button is, through UI Automation.
    // The Windows 11 taskbar is XAML, so its buttons have no window handles to measure.
    // This runs on its own thread: the overlay is a child of the taskbar, and a cross-process
    // UI Automation call from the overlay's UI thread could deadlock with Explorer.
    internal sealed class TaskbarProbe : IDisposable
    {
        public sealed class Result
        {
            public int TasksRight = -1;               // right edge of the rightmost Start, search or app button
            public int WidgetsLeft = -1, WidgetsRight = -1; // -1 when the weather button is off
            public DateTime At;
        }

        const int IntervalMs = 3000;
        volatile Result latest;
        volatile bool run = true;
        bool failureLogged;

        public Result Latest { get { return latest; } }

        public TaskbarProbe()
        {
            var t = new Thread(Loop) { IsBackground = true, Name = "taskbar probe" };
            t.SetApartmentState(ApartmentState.MTA);
            t.Start();
        }

        void Loop()
        {
            while (run)
            {
                try { latest = Probe(); failureLogged = false; }
                catch (Exception e)
                {
                    latest = null;
                    if (!failureLogged) { Log.Write("taskbar probe: " + e.Message); failureLogged = true; }
                }
                for (int i = 0; i < IntervalMs / 100 && run; i++) Thread.Sleep(100);
            }
        }

        public static Result Probe()
        {
            IntPtr bar = Native.FindWindow("Shell_TrayWnd", null);
            if (bar == IntPtr.Zero) throw new InvalidOperationException("taskbar not found");
            var root = AutomationElement.FromHandle(bar);
            var cache = new CacheRequest();
            cache.Add(AutomationElement.ClassNameProperty);
            cache.Add(AutomationElement.AutomationIdProperty);
            cache.Add(AutomationElement.BoundingRectangleProperty);
            AutomationElementCollection buttons;
            using (cache.Activate())
                buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

            var r = new Result { At = DateTime.UtcNow };
            foreach (AutomationElement e in buttons)
            {
                var rect = (System.Windows.Rect)e.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty);
                if (rect.IsEmpty || rect.Width <= 0) continue;
                string cls = e.GetCachedPropertyValue(AutomationElement.ClassNameProperty) as string ?? "";
                string id = e.GetCachedPropertyValue(AutomationElement.AutomationIdProperty) as string ?? "";
                if (id == "WidgetsButton") { r.WidgetsLeft = (int)rect.Left; r.WidgetsRight = (int)Math.Ceiling(rect.Right); }
                else if (!cls.StartsWith("SystemTray.")) r.TasksRight = Math.Max(r.TasksRight, (int)Math.Ceiling(rect.Right));
            }
            return r;
        }

        public void Dispose() { run = false; }
    }
}
