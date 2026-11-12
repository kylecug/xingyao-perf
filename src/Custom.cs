using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace XingyaoPerf
{
    // One of the three custom scenes. Saved in settings.ini as custom1..custom3.
    internal sealed class Profile
    {
        public const int FanEc = 0, FanQuiet = 1, FanCurve = 2;
        public const int PowerEfficiency = 0, PowerBalanced = 1, PowerPerformance = 2;

        public string Name;
        public int Mode = 2;                  // official mode 1-3
        public int Power = PowerBalanced;     // Windows power mode
        public int Cpu;                       // maximum processor state in percent, 0 = no limit
        public int Gpu;                       // GPU cap in MHz, 0 = no limit
        public int Fan = FanCurve;
        // Fan curve: below Temps[0] the fans run at Pcts[0], from Temps[i] at Pcts[i + 1], and from Temps[3] the EC takes over.
        // Percent 0 stops the fans; otherwise 30-100, the range verified for D6.
        public int[] Temps = { 58, 68, 78, 85 };
        public int[] Pcts = { 0, 30, 45, 65 };

        // The first two start as the everyday quiet and full power modes; the third is a plain example.
        public static Profile Default(int i)
        {
            if (i == 1) return new Profile { Name = "日常静音模式", Mode = 1, Power = PowerEfficiency, Gpu = 1200, Fan = FanQuiet };
            if (i == 2) return new Profile { Name = "火力全开模式", Mode = 3, Power = PowerPerformance, Fan = FanEc };
            return new Profile { Name = "自定义 " + i };
        }

        public string Serialize()
        {
            return Name.Replace("|", "/").Replace("=", "-") + "|" + Mode + "|" + Power + "|" + Cpu + "|" + Gpu + "|" + Fan + "|" + Join(Temps) + "|" + Join(Pcts);
        }

        static string Join(int[] a) { return string.Join(",", Array.ConvertAll(a, x => x.ToString(CultureInfo.InvariantCulture))); }

        static int[] Split(string s)
        {
            var parts = s.Split(',');
            var r = new int[parts.Length];
            for (int i = 0; i < parts.Length; i++) r[i] = int.Parse(parts[i], CultureInfo.InvariantCulture);
            return r;
        }

        public static Profile Parse(string v, int i)
        {
            try
            {
                var f = v.Split('|');
                var p = new Profile { Name = f[0], Mode = int.Parse(f[1]), Power = int.Parse(f[2]), Cpu = int.Parse(f[3]), Gpu = int.Parse(f[4]), Fan = int.Parse(f[5]), Temps = Split(f[6]), Pcts = Split(f[7]) };
                if (p.Check() == null) return p;
            }
            catch (Exception) { }
            Log.Write("custom" + i + ": invalid, using defaults");
            return Default(i);
        }

        // Null if valid, otherwise what is wrong.
        public string Check()
        {
            if (string.IsNullOrEmpty(Name) || Name.Trim().Length == 0) return "名称不能为空";
            if (Mode < 1 || Mode > 3 || Power < 0 || Power > 2 || Fan < 0 || Fan > 2) return "选项无效";
            if (Cpu != 0 && (Cpu < 50 || Cpu > 99)) return "CPU 选项无效";
            if (Gpu < 0) return "GPU 选项无效";
            if (Temps == null || Temps.Length != 4 || Pcts == null || Pcts.Length != 4) return "风扇曲线无效";
            for (int i = 0; i < 4; i++)
            {
                if (Temps[i] < 30 || Temps[i] > 90) return "温度要在 30 到 90 °C 之间";
                if (i > 0 && Temps[i] <= Temps[i - 1]) return "温度要从左到右递增";
                if (Pcts[i] != 0 && (Pcts[i] < 30 || Pcts[i] > 100)) return "风扇只能停转或 30% 到 100%";
            }
            return null;
        }

        public Profile Clone()
        {
            var p = (Profile)MemberwiseClone();
            p.Temps = (int[])Temps.Clone();
            p.Pcts = (int[])Pcts.Clone();
            return p;
        }
    }

    // Settings window for the three custom scenes, one tab each.
    internal sealed class CustomForm : Form
    {
        static readonly string[] ModeNames = { "办公模式", "平衡模式", "性能模式" };
        static readonly string[] PowerNames = { "最佳能效", "平衡", "最佳性能" };
        static readonly int[] CpuValues = { 0, 99, 80, 70 };
        static readonly string[] CpuNames = { "不限制", "关闭睿频（约 1.8 GHz）", "约 1.5 GHz", "约 1.3 GHz" };
        static readonly int[] GpuValues = { 0, 2000, 1600, 1200, 900 };
        static readonly string[] FanNames = { "EC 曲线", "静音（低温停转，30%，再交还 EC）", "自定义曲线" };
        static readonly int[] PctValues = { 0, 30, 35, 40, 45, 50, 55, 60, 65, 70, 75, 80, 85, 90, 95, 100 };

        sealed class Page
        {
            public TextBox Name;
            public ComboBox Mode, Power, Cpu, Gpu, Fan;
            public NumericUpDown[] Temps = new NumericUpDown[4];
            public ComboBox[] Pcts = new ComboBox[4];
            public Control Curve;
        }

        readonly Page[] pages = new Page[3];
        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };

        // Called with the edited profiles and the index of the tab to switch to, or -1 to only save.
        public event Action<Profile[], int> Saved;

        public CustomForm(Profile[] profiles, int selected)
        {
            Text = "自定义模式";
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Font;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ShowInTaskbar = true;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;

            for (int i = 0; i < 3; i++) tabs.TabPages.Add(BuildPage(i, profiles[i]));
            tabs.SelectedIndex = Math.Max(0, Math.Min(2, selected));
            tabs.Size = tabs.TabPages[0].Controls[0].PreferredSize + new Size(24, 40);

            var apply = new Button { Text = "应用并切换到此模式", AutoSize = true };
            var save = new Button { Text = "保存", AutoSize = true };
            var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
            apply.Click += (s, e) => Finish(tabs.SelectedIndex);
            save.Click += (s, e) => Finish(-1);
            cancel.Click += (s, e) => Close();
            CancelButton = cancel;
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 6, 0, 0) };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(save);
            buttons.Controls.Add(apply);

            var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(10) };
            root.Controls.Add(tabs);
            root.Controls.Add(buttons);
            Controls.Add(root);
        }

        static ComboBox Combo(string[] items, int index)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
            c.Items.AddRange(items);
            c.SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, index));
            return c;
        }

        const int CellW = 72;

        static Label Cell(string text)
        {
            return new Label { Text = text, AutoSize = false, Width = CellW, Height = 24, TextAlign = ContentAlignment.MiddleCenter };
        }

        TabPage BuildPage(int i, Profile p)
        {
            var g = new Page();
            pages[i] = g;
            var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Padding = new Padding(6) };
            Action<string, Control> row = (label, ctl) =>
            {
                t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 7, 12, 3) });
                t.Controls.Add(ctl);
            };
            g.Name = new TextBox { Text = p.Name, Width = 220, MaxLength = 20 };
            g.Mode = Combo(ModeNames, p.Mode - 1);
            g.Power = Combo(PowerNames, p.Power);
            g.Cpu = Combo(CpuNames, Array.IndexOf(CpuValues, p.Cpu));
            g.Gpu = Combo(Array.ConvertAll(GpuValues, v => v == 0 ? "不限制" : (v / 1000.0).ToString("0.0") + " GHz"), Array.IndexOf(GpuValues, p.Gpu));
            g.Fan = Combo(FanNames, p.Fan);
            row("名称", g.Name);
            row("官方模式", g.Mode);
            row("Windows 电源模式", g.Power);
            row("CPU 最高频率", g.Cpu);
            row("GPU 最高频率", g.Gpu);
            row("风扇", g.Fan);

            // Curve: four temperatures and four speeds; the last column hands the fans to the EC.
            var c = new TableLayoutPanel { AutoSize = true, ColumnCount = 6, Margin = new Padding(0, 6, 0, 0) };
            c.Controls.Add(new Label { Text = "从温度 °C", AutoSize = true, Anchor = AnchorStyles.Left });
            c.Controls.Add(Cell("更低"));
            for (int k = 0; k < 4; k++)
            {
                g.Temps[k] = new NumericUpDown { Minimum = 30, Maximum = 90, Value = p.Temps[k], Width = CellW, TextAlign = HorizontalAlignment.Center };
                c.Controls.Add(g.Temps[k]);
            }
            c.Controls.Add(new Label { Text = "风扇", AutoSize = true, Anchor = AnchorStyles.Left });
            for (int k = 0; k < 4; k++)
            {
                g.Pcts[k] = Combo(Array.ConvertAll(PctValues, v => v == 0 ? "停转" : v + "%"), Array.IndexOf(PctValues, p.Pcts[k]));
                g.Pcts[k].Width = CellW;
                c.Controls.Add(g.Pcts[k]);
            }
            c.Controls.Add(Cell("交还 EC"));
            g.Curve = c;
            t.Controls.Add(new Label { Text = "风扇曲线", AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(3, 12, 12, 3) });
            t.Controls.Add(c);
            t.Controls.Add(new Label());
            t.Controls.Add(new Label { Text = "每格是从这个温度起的风扇转速。按 5 秒平均温度换档；\n降温要比该点低 3 °C 并保持 20 秒。读不到温度、睡眠或退出时交还 EC。", AutoSize = true, ForeColor = SystemColors.GrayText });
            g.Fan.SelectedIndexChanged += (s, e) => c.Enabled = g.Fan.SelectedIndex == Profile.FanCurve;
            c.Enabled = g.Fan.SelectedIndex == Profile.FanCurve;
            g.Name.TextChanged += (s, e) => tabs.TabPages[i].Text = g.Name.Text.Length == 0 ? "自定义 " + (i + 1) : g.Name.Text;

            var page = new TabPage(p.Name) { UseVisualStyleBackColor = true };
            page.Controls.Add(t);
            return page;
        }

        Profile Read(int i)
        {
            var g = pages[i];
            var p = new Profile
            {
                Name = g.Name.Text.Trim(),
                Mode = g.Mode.SelectedIndex + 1,
                Power = g.Power.SelectedIndex,
                Cpu = CpuValues[g.Cpu.SelectedIndex],
                Gpu = GpuValues[g.Gpu.SelectedIndex],
                Fan = g.Fan.SelectedIndex,
            };
            for (int k = 0; k < 4; k++) { p.Temps[k] = (int)g.Temps[k].Value; p.Pcts[k] = PctValues[g.Pcts[k].SelectedIndex]; }
            return p;
        }

        void Finish(int switchTo)
        {
            var result = new Profile[3];
            for (int i = 0; i < 3; i++)
            {
                result[i] = Read(i);
                string problem = result[i].Check();
                if (problem != null)
                {
                    tabs.SelectedIndex = i;
                    MessageBox.Show(this, problem, "自定义模式", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            var h = Saved;
            if (h != null) h(result, switchTo);
            Close();
        }
    }
}
