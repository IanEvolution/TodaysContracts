// One Check v2 — a tiny daily punch card for Windows.
// One task per day. Punch it when it's done. Set tomorrow's before you stop.
// Visual direction: acid graphic-design sci-fi (flat color fields, hard edges, wide type, micro text).
// Targets .NET Framework 4.x (built into Windows 10/11). No installs needed.
// Written for C# 5 so the compiler that ships with Windows can build it (see build.bat).

using System;
using System.Collections.Generic;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Today's Contracts")]
[assembly: System.Reflection.AssemblyProduct("Today's Contracts")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]

namespace OneCheck
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

        [STAThread]
        static void Main()
        {
            bool fresh;
            using (var mutex = new Mutex(true, "OneCheck.SingleInstance.v1", out fresh))
            {
                if (!fresh)
                {
                    IntPtr h = FindWindow(null, "Today's Contracts");   // must match MainForm.Text exactly
                    if (h != IntPtr.Zero) { ShowWindow(h, 9); SetForegroundWindow(h); }
                    return;
                }
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                GC.KeepAlive(mutex);
            }
        }
    }

    class Day
    {
        public string Task = "";
        public bool Done;
        public string DoneAt = "";
    }

    class Step
    {
        public string Text = "";
        public bool Done;
    }

    // a repeating chore: shows up on its weekdays until it's checked
    class Routine
    {
        public string Id = "";
        public bool[] Days = new bool[7];   // Sunday first
        public string Created = "";          // yyyy-MM-dd; days before this are ignored
        public string Text = "";
    }

    // a big idea parked in the vault until it gets queued as tomorrow's task
    class Idea
    {
        public string Added = "";   // yyyy-MM-dd
        public string Text = "";
    }

    class Accent
    {
        public string Name; public Color Color;
        public Accent(string n, string hex) { Name = n; Color = ColorTranslator.FromHtml(hex); }
    }

    // ------------------------------------------------------------------
    // Sounds: all synthesized in code at startup (a few KB each), played
    // straight from memory through winmm. Nothing is loaded from disk.
    // ------------------------------------------------------------------
    static class Sfx
    {
        [DllImport("winmm.dll")] static extern bool PlaySound(IntPtr snd, IntPtr hmod, uint flags);
        const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_MEMORY = 0x4;
        const int SR = 22050;

        public static byte[] Tick, Click, Punch, Boot, Undo, Save;
        static readonly List<GCHandle> pins = new List<GCHandle>();
        static DateTime busyUntil = DateTime.MinValue;

        public static void Init()
        {
            Tick = Wav(MakeTick()); Click = Wav(MakeClick()); Undo = Wav(MakeUndo()); Save = Wav(MakeSave());
            // punch + startup come from WAV files built into the exe (see build.bat);
            // if they're missing, fall back to the synthesized ones
            Punch = Wav(MakeClassicPunch(), false);
            Boot = Resource("boot.wav") ?? Wav(MakeBoot());
        }

        static byte[] Resource(string name)
        {
            try
            {
                using (var st = typeof(Sfx).Assembly.GetManifestResourceStream(name))
                {
                    if (st == null) return null;
                    var ms = new MemoryStream(); st.CopyTo(ms); return ms.ToArray();
                }
            }
            catch { return null; }
        }

        public static bool IsWav(byte[] b)
        {
            return b != null && b.Length > 44 && Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && Encoding.ASCII.GetString(b, 8, 4) == "WAVE";
        }

        static double Seconds(byte[] wav)
        {
            try { int byteRate = BitConverter.ToInt32(wav, 28); return byteRate > 0 ? (wav.Length - 44) / (double)byteRate : 1; }
            catch { return 1; }
        }

        // priority sounds (punch/boot) aren't cut off by hover ticks
        public static void Play(byte[] wav, bool priority)
        {
            if (wav == null) return;
            if (!priority && DateTime.Now < busyUntil) return;
            try
            {
                var h = GCHandle.Alloc(wav, GCHandleType.Pinned);
                pins.Add(h);
                PlaySound(h.AddrOfPinnedObject(), IntPtr.Zero, SND_ASYNC | SND_NODEFAULT | SND_MEMORY);
                if (priority) busyUntil = DateTime.Now.AddSeconds(Math.Min(3, Seconds(wav)));
                // release pins for sounds that have certainly finished
                while (pins.Count > 4) { pins[0].Free(); pins.RemoveAt(0); }
            }
            catch { }
        }

        static double Sq(double f, double t) { return Math.Sin(2 * Math.PI * f * t) >= 0 ? 1 : -1; }
        static double Crush(double v, int bits) { double l = (1 << (bits - 1)); return Math.Round(v * l) / l; }

        static double[] MakeTick()
        {
            int n = (int)(SR * 0.016); var s = new double[n];
            for (int i = 0; i < n; i++) { double t = (double)i / SR; s[i] = Sq(3100, t) * Math.Exp(-t * 260) * 0.10; }
            return s;
        }

        static double[] MakeClick()
        {
            int n = (int)(SR * 0.06); var s = new double[n];
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / SR;
                double f = t < 0.018 ? 1250 : 1870;
                s[i] = Crush(Sq(f, t) * Math.Exp(-t * 55) * 0.26, 5);
            }
            return s;
        }

        static double[] MakeSave()
        {
            int n = (int)(SR * 0.11); var s = new double[n];
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / SR, v = 0;
                if (t < 0.04) v = Sq(1320, t) * Math.Exp(-t * 60);
                else if (t > 0.05) v = Sq(1980, t) * Math.Exp(-(t - 0.05) * 50);
                s[i] = Crush(v * 0.22, 5);
            }
            return s;
        }

        static double[] MakeUndo()
        {
            int n = (int)(SR * 0.14); var s = new double[n];
            double ph = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / SR;
                double f = 900 * Math.Exp(-t * 9);
                ph += f / SR;
                s[i] = Crush(((ph % 1) < 0.5 ? 1 : -1) * Math.Exp(-t * 18) * 0.24, 4);
            }
            return s;
        }

        // The original v1 punch: a click, a metal tick and a deep low thump.
        static double[] MakeClassicPunch()
        {
            int n = (int)(SR * 0.2); var s = new double[n];
            var rnd = new Random(7);
            double ph = 0;
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / SR;
                double f = 55 + 120 * Math.Exp(-t * 28);
                ph += 2 * Math.PI * f / SR;
                double thump = Math.Sin(ph) * Math.Exp(-t * 20);
                double click = t < 0.008 ? (rnd.NextDouble() * 2 - 1) * Math.Exp(-t * 500) : 0;
                double tick = Math.Sin(2 * Math.PI * 2300 * t) * Math.Exp(-t * 110) * 0.22;
                s[i] = Math.Max(-1, Math.Min(1, 0.85 * thump + 0.55 * click + tick)) * (26000.0 / 28000.0);
            }
            return s;
        }

        static double[] MakeBoot()
        {
            int n = (int)(SR * 0.55); var s = new double[n];
            double ph = 0; var rnd = new Random(3);
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / SR, v = 0;
                double f = 110 * Math.Pow(8, Math.Min(1, t / 0.38));
                ph += f / SR;
                double saw = 2 * (ph % 1) - 1;
                double gate = t < 0.16 ? (((int)(t / 0.028)) % 2 == 0 ? 1 : 0.15) : 1;
                if (t < 0.40) v += saw * 0.16 * gate * (0.4 + t);
                if (t >= 0.40) v += Sq(1760, t) * Math.Exp(-(t - 0.40) * 30) * 0.2;
                if (t >= 0.40 && t < 0.43) v += (rnd.NextDouble() * 2 - 1) * 0.15;
                s[i] = Crush(v, 5);
            }
            // sample-and-hold for extra grit
            for (int i = 0; i < n; i++) if (i % 3 != 0) s[i] = s[i - i % 3];
            return s;
        }

        static double[] MakePunch()
        {
            int n = (int)(SR * 0.8); var s = new double[n];
            var rnd = new Random(11);
            double ph = 0;
            double[] notes = { 880, 1320, 1760 };
            for (int i = 0; i < n; i++)
            {
                double t = (double)i / SR, v = 0;
                // sub drop
                double f = 42 + 150 * Math.Exp(-t * 32);
                ph += 2 * Math.PI * f / SR;
                if (t < 0.5) v += Math.Sin(ph) * Math.Exp(-t * 8) * 0.9;
                // crunchy transient
                if (t < 0.06) v += (rnd.NextDouble() * 2 - 1) * Math.Exp(-t * 60) * 0.7;
                // confirm arpeggio + a quieter echo
                for (int k = 0; k < 3; k++)
                {
                    double t0 = 0.05 + k * 0.06;
                    if (t >= t0 && t < t0 + 0.12) v += Sq(notes[k], t) * Math.Exp(-(t - t0) * 22) * 0.2;
                    double e0 = t0 + 0.2;
                    if (t >= e0 && t < e0 + 0.12) v += Sq(notes[k], t) * Math.Exp(-(t - e0) * 22) * 0.07;
                }
                // low tail chord
                if (t >= 0.28) v += (Sq(220, t) + Sq(330, t)) * Math.Exp(-(t - 0.28) * 5.5) * 0.07;
                s[i] = Crush(Math.Tanh(v * 1.2) * 0.85, 7);
            }
            for (int i = 0; i < n; i++) if (i % 2 != 0) s[i] = s[i - 1];
            return s;
        }

        static byte[] Wav(double[] s) { return Wav(s, true); }

        static byte[] Wav(double[] s, bool fadeIn)
        {
            int n = s.Length;
            var ms = new MemoryStream();
            var w = new BinaryWriter(ms);
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + n * 2);
            w.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(SR); w.Write(SR * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(n * 2);
            for (int i = 0; i < n; i++)
            {
                double v = Math.Max(-1, Math.Min(1, s[i]));
                // 3ms fade in/out to avoid clicks at the edges
                double edge = Math.Min(1, Math.Min(fadeIn ? i : int.MaxValue, n - 1 - i) / (SR * 0.003));
                w.Write((short)(v * edge * 28000));
            }
            w.Flush();
            return ms.ToArray();
        }
    }

    class MainForm : Form, IMessageFilter
    {
        // ---------- look ----------
        static readonly Accent[] Accents = {
            new Accent("Marathon",    "#C3FC0D"),
            new Accent("SekiGuchi",    "#63EDE0"),
            new Accent("UESC",  "#470BF6"),
            new Accent("NuCaloric", "#F81D78"),
            new Accent("Traxus",  "#FD6C1D"),
            new Accent("Mida",    "#C6A8FF"),
            new Accent("Arachne", "#E8102A"),
        };

        Color cBg, cPanel, cInk, cMuted, cLine, cHole, cField, cDim;
        Color accent = Accents[0].Color;
        string accentName = "Acid";
        bool dark = true, sound = true, bootSound = true, uiSound = true, onTop = false;
        bool rotate;                // daily rotation through the Accents presets
        string rotateShown = "";    // last date the FACTION flash was shown
        byte[] customPunch, customBoot;   // user-chosen WAVs, copied into the data folder
        string punchName = "", bootName = "";

        float S = 1f; // DPI scale
        const int LW = 384, LH = 690; // logical size
        const int Pad = 18;
        const float WIDE = 1.22f; // horizontal stretch for display type

        Font fHuge, fMid, fBtn, fMicro, fMicroB, fTask, fTaskSmall, fBody, fCell;
        Font fHugeFit; float fHugeFitSize;   // shrunk copy of fHuge for long day names (cached)
        Font fTitleFit; float fTitleFitSize; // shrunk copy of fMid if the title bar text would hit the window buttons (cached)

        // ---------- data ----------
        readonly Dictionary<string, Day> days = new Dictionary<string, Day>();
        readonly Dictionary<string, List<Step>> steps = new Dictionary<string, List<Step>>();
        const int MaxSteps = 50;    // effectively unlimited; just keeps steps.txt sane
        const int VisibleSteps = 6; // rows shown at once; the list scrolls past this
        readonly List<Routine> routines = new List<Routine>();
        readonly Dictionary<string, List<string>> routineLog = new Dictionary<string, List<string>>(); // id -> dates done
        const int MaxRoutines = 8, MaxRoutineLog = 20;
        const int MaxVault = 3;
        readonly Idea[] vault = new Idea[MaxVault];   // fixed slots; null = empty
        string dataDir, daysFile, stepsFile, settingsFile, routinesFile, routineLogFile, vaultFile;
        string todayKey;

        // ---------- ui state ----------
        bool editing, showSettings, showRoutines;   // showRoutines is a sub-screen of settings
        string hover = "";
        readonly Dictionary<string, Rectangle> hits = new Dictionary<string, Rectangle>();
        TextBox txtToday, txtTomorrow, txtStep, txtRoutine, txtVault;
        Rectangle fieldToday, fieldTomorrow, fieldStep, fieldRoutine, fieldVault;
        float punchAnim = 1f, bootAnim = 1f;
        DateTime punchStart, bootStart;
        System.Windows.Forms.Timer animTimer, tickTimer, flashTimer, confirmTimer;
        string flash = "";
        int frame;
        int stepScroll;             // index of the first visible step row
        int wheelAcc;               // leftover wheel delta (precision touchpads send fractions of a notch)
        bool sbDrag; int sbDragOff; // dragging the steps scrollbar thumb
        // mini punch when a step is checked, and the punch button unlocking when the last item is
        const float StepAnimMs = 350f, UnlockMs = 300f;
        int stepAnimIndex = -1;     // step being stamped, -1 = none
        DateTime stepAnimStart, unlockStart;   // unlockStart can be in the future: it waits for the step stamp
        bool unlockOn;
        // "QUEUED" glitch on the NEXT field when tomorrow's task is saved: a shorter, lighter punch glitch
        const float NextAnimMs = 340f;       // total length (the punch runs 520)
        const float NextAnimSplit = 0.55f;   // share of it spent glitching in; the rest settles back to the field
        const float NextAnimJitter = 10f;    // max slice jitter (the punch uses 18)
        bool nextAnimOn;
        DateTime nextAnimStart;
        // vault animations, one per slot: kind + start time; progress is worked out from the clock while painting
        const int VNone = 0, VStore = 1, VMove = 2, VDelete = 3;
        const float VStoreMs = 250f, VMoveMs = 350f, VDeleteMs = 200f;
        readonly int[] vAnimKind = new int[MaxVault];
        readonly DateTime[] vAnimStart = new DateTime[MaxVault];
        readonly Idea[] vGhost = new Idea[MaxVault];   // the idea that just left the slot (move / delete)
        int vConfirm = -1;                             // slot whose delete is waiting for a second click

        public MainForm()
        {
            Text = "Today's Contracts";   // Program.Main finds the window by this exact title
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;
            KeyPreview = true;
            ShowInTaskbar = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            using (var g = CreateGraphics()) S = g.DpiX / 96f;
            ClientSize = new Size(D(LW), D(LH));

            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OneCheck");
            Directory.CreateDirectory(dataDir);
            daysFile = Path.Combine(dataDir, "days.txt");
            stepsFile = Path.Combine(dataDir, "steps.txt");
            settingsFile = Path.Combine(dataDir, "settings.txt");
            routinesFile = Path.Combine(dataDir, "routines.txt");
            routineLogFile = Path.Combine(dataDir, "routine_log.txt");
            vaultFile = Path.Combine(dataDir, "vault.txt");

            MakeFonts();
            LoadSettings();
            FixStartupPath();
            ApplyRotation();
            ApplyTheme();
            LoadDays();
            LoadSteps();
            LoadRoutines();
            LoadVault();
            todayKey = Key(DateTime.Today);
            Sfx.Init();

            txtToday = MakeBox();
            txtTomorrow = MakeBox();
            txtToday.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SetToday(); }
                else if (e.KeyCode == Keys.Escape && Get(todayKey).Task != "") { e.SuppressKeyPress = true; editing = false; Relayout(); }
            };
            txtTomorrow.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; SaveTomorrow(); }
            };
            txtStep = MakeBox();
            txtStep.MaxLength = 90;
            txtStep.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddStep(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; txtStep.Text = ""; ActiveControl = null; Invalidate(); }
            };
            txtRoutine = MakeBox();
            txtRoutine.MaxLength = 60;
            txtRoutine.KeyDown += (s, e) =>
            {   // Escape is handled by the form (goes back one screen)
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddRoutine(); }
            };
            Controls.Add(txtToday);
            Controls.Add(txtTomorrow);
            Controls.Add(txtStep);
            txtVault = MakeBox();
            txtVault.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; AddVault(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; txtVault.Text = ""; ActiveControl = null; Invalidate(); }
            };
            txtTomorrow.TextChanged += (s, e) => Invalidate();   // the vault's NEXT buttons lock while this has text
            Controls.Add(txtRoutine);
            Controls.Add(txtVault);
            foreach (var tb in new[] { txtToday, txtTomorrow, txtStep, txtRoutine, txtVault })
            {
                tb.GotFocus += (s, e) => Invalidate();
                tb.LostFocus += (s, e) => Invalidate();
            }
            txtToday.HandleCreated += (s, e) => Cue(txtToday, "Input today's directive");
            txtTomorrow.HandleCreated += (s, e) => Cue(txtTomorrow, "Queue it before you stop today");
            txtStep.HandleCreated += (s, e) => Cue(txtStep, "+ Add a step, press Enter");
            txtRoutine.HandleCreated += (s, e) => Cue(txtRoutine, "+ New routine, press Enter");
            txtVault.HandleCreated += (s, e) => Cue(txtVault, "Store an idea in the vault");

            // animation timer only runs during the boot wipe, the punch glitch, a step stamp, the punch unlock,
            // the NEXT "QUEUED" glitch and vault animations
            animTimer = new System.Windows.Forms.Timer { Interval = 16 };
            animTimer.Tick += (s, e) =>
            {
                frame++;
                bool was = bootAnim < 1f || punchAnim < 1f;
                bootAnim = Math.Min(1f, (float)(DateTime.Now - bootStart).TotalMilliseconds / 650f);
                punchAnim = Math.Min(1f, (float)(DateTime.Now - punchStart).TotalMilliseconds / 520f);
                if (StepAnim >= 1f) stepAnimIndex = -1;
                if (UnlockAnim >= 1f) unlockOn = false;
                if (nextAnimOn && NextAnim >= 1f) { nextAnimOn = false; Relayout(); }   // brings the NEXT text box back
                bool busy = bootAnim < 1f || punchAnim < 1f;
                if (was && !busy) Relayout();
                bool vaultBusy = VaultAnimTick();
                if (!busy && stepAnimIndex < 0 && !unlockOn && !nextAnimOn && !vaultBusy) animTimer.Stop();
                Invalidate();
            };
            // checks once a minute whether the date rolled over; nothing else runs while idle
            tickTimer = new System.Windows.Forms.Timer { Interval = 60000 };
            tickTimer.Tick += (s, e) => CheckRollover();
            tickTimer.Start();
            flashTimer = new System.Windows.Forms.Timer { Interval = 1600 };
            flashTimer.Tick += (s, e) => { flashTimer.Stop(); flashTimer.Interval = 1600; flash = ""; Invalidate(); };
            // one-shot: a vault delete that isn't confirmed in time goes back to "x"
            confirmTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            confirmTimer.Tick += (s, e) => { confirmTimer.Stop(); vConfirm = -1; Invalidate(); };

            Activated += (s, e) => CheckRollover();
            Shown += (s, e) =>
            {
                if (Get(todayKey).Task != "") ActiveControl = null;
                bootAnim = 0f; punchAnim = 1f; bootStart = DateTime.Now; animTimer.Start();
                Relayout();
                AnnounceFaction();
                if (bootSound) Sfx.Play(customBoot ?? Sfx.Boot, true);
            };
            TopMost = onTop;
            RestorePosition();
            Relayout();
            Application.AddMessageFilter(this);   // mouse wheel over the steps list, whatever has focus
#if PREVIEW
            showSettings = Environment.GetEnvironmentVariable("OC_SETTINGS") == "1";
            Relayout();
#endif
        }

        // ---------- helpers ----------
        int D(float v) { return (int)Math.Round(v * S); }
        Rectangle R(float x, float y, float w, float h) { return new Rectangle(D(x), D(y), D(w), D(h)); }
        static string Key(DateTime d) { return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture); }
        Day Get(string k) { Day d; return days.TryGetValue(k, out d) ? d : new Day(); }
        static Color Hex(string h) { return ColorTranslator.FromHtml(h); }

        static Color Blend(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        static Color Fade(Color c, int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c); }

        static Color OnColor(Color c)
        {
            double l = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
            return l > 140 ? Color.FromArgb(10, 11, 13) : Color.FromArgb(240, 242, 236);
        }

        // accent used as text: on the light theme, darken it so it stays readable
        Color AccentText
        {
            get
            {
                double l = 0.2126 * accent.R + 0.7152 * accent.G + 0.0722 * accent.B;
                if (dark) return l < 110 ? Blend(accent, Color.White, 0.45f) : accent;   // lift deep colors like Violet
                return l > 110 ? Blend(accent, Color.Black, 0.45f) : accent;
            }
        }

        Font TryFont(string[] names, float size, FontStyle style)
        {
            foreach (var n in names)
            {
                try
                {
                    var f = new Font(n, size, style);
                    if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) return f;
                    f.Dispose();
                }
                catch { }
            }
            return new Font(FontFamily.GenericSansSerif, size, style);
        }

        void MakeFonts()
        {
            var display = new[] { "Bahnschrift", "Segoe UI" };
            var ui = new[] { "Segoe UI" };
            var uiSemi = new[] { "Segoe UI Semibold", "Segoe UI" };
            var mono = new[] { "Cascadia Mono", "Consolas", "Courier New" };
            fHuge = TryFont(display, 30f, FontStyle.Bold);
            fMid = TryFont(display, 13f, FontStyle.Bold);
            fBtn = TryFont(display, 15f, FontStyle.Bold);
            fMicro = TryFont(mono, 7.5f, FontStyle.Regular);
            fMicroB = TryFont(mono, 7.5f, FontStyle.Bold);
            fCell = TryFont(mono, 7.5f, FontStyle.Bold);
            fTask = TryFont(uiSemi, 13f, FontStyle.Regular);
            fTaskSmall = TryFont(uiSemi, 10.5f, FontStyle.Regular);
            fBody = TryFont(ui, 10.5f, FontStyle.Regular);
        }

        void ApplyTheme()
        {
            if (dark)
            {
                cBg = Hex("#0A0B0D"); cPanel = Hex("#111316"); cInk = Hex("#ECEEE6"); cMuted = Hex("#7D847E");
                cLine = Hex("#2B3036"); cHole = Hex("#1A1D21"); cField = Hex("#0A0B0D"); cDim = Hex("#3A3F45");
            }
            else
            {
                cBg = Hex("#E4E2DA"); cPanel = Hex("#EEEDE7"); cInk = Hex("#0B0C0E"); cMuted = Hex("#5E615C");
                cLine = Hex("#B4B2A9"); cHole = Hex("#DAD8CF"); cField = Hex("#F7F6F1"); cDim = Hex("#C4C2B9");
            }
            BackColor = cBg;
            foreach (Control c in Controls) { c.BackColor = cField; c.ForeColor = cInk; }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        static void Cue(TextBox t, string s) { try { SendMessage(t.Handle, 0x1501, (IntPtr)1, s); } catch { } }

        TextBox MakeBox()
        {
            return new TextBox { BorderStyle = BorderStyle.None, Font = fBody, BackColor = cField, ForeColor = cInk, MaxLength = 200 };
        }

        void Sound(byte[] wav) { if (uiSound) Sfx.Play(wav, false); }

        // ---------- storage ----------
        void LoadDays()
        {
            days.Clear();
            try
            {
                if (!File.Exists(daysFile)) return;
                foreach (var line in File.ReadAllLines(daysFile, Encoding.UTF8))
                {
                    var p = line.Split(new[] { '\t' }, 4);
                    if (p.Length < 4 || p[0].Length != 10) continue;
                    days[p[0]] = new Day { Done = p[1] == "1", DoneAt = p[2], Task = p[3] };
                }
            }
            catch { }
        }

        List<Step> Steps(string k) { List<Step> l; return steps.TryGetValue(k, out l) ? l : new List<Step>(); }

        void LoadSteps()
        {
            steps.Clear();
            try
            {
                if (!File.Exists(stepsFile)) return;
                foreach (var line in File.ReadAllLines(stepsFile, Encoding.UTF8))
                {
                    var p = line.Split(new[] { '\t' }, 3);
                    if (p.Length < 3 || p[0].Length != 10) continue;
                    List<Step> l;
                    if (!steps.TryGetValue(p[0], out l)) { l = new List<Step>(); steps[p[0]] = l; }
                    l.Add(new Step { Done = p[1] == "1", Text = p[2] });
                }
            }
            catch { }
        }

        void SaveSteps()
        {
            try
            {
                var keys = new List<string>(steps.Keys); keys.Sort();
                var sb = new StringBuilder();
                foreach (var k in keys)
                    foreach (var st in steps[k])
                        sb.Append(k).Append('\t').Append(st.Done ? "1" : "0").Append('\t')
                          .Append(st.Text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')).Append("\r\n");
                string tmp = stepsFile + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(stepsFile)) File.Replace(tmp, stepsFile, null); else File.Move(tmp, stepsFile);
            }
            catch (Exception ex) { flash = "SAVE FAILED: " + ex.Message; flashTimer.Start(); }
        }

        void SaveDays()
        {
            try
            {
                var keys = new List<string>(days.Keys); keys.Sort();
                var sb = new StringBuilder();
                foreach (var k in keys)
                {
                    var d = days[k];
                    if (d.Task == "" && !d.Done) continue;
                    sb.Append(k).Append('\t').Append(d.Done ? "1" : "0").Append('\t').Append(d.DoneAt).Append('\t')
                      .Append(d.Task.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ')).Append("\r\n");
                }
                string tmp = daysFile + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Encoding.UTF8);
                if (File.Exists(daysFile)) File.Replace(tmp, daysFile, null); else File.Move(tmp, daysFile);
            }
            catch (Exception ex) { flash = "SAVE FAILED: " + ex.Message; flashTimer.Start(); }
        }

        // write to a .tmp file first, then swap it in, so a crash never leaves a half-written file
        static void WriteAtomic(string file, string text)
        {
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, text, Encoding.UTF8);
            if (File.Exists(file)) File.Replace(tmp, file, null); else File.Move(tmp, file);
        }

        static string OneLine(string s) { return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '); }

        void LoadRoutines()
        {
            routines.Clear();
            routineLog.Clear();
            try
            {
                if (File.Exists(routinesFile))
                    foreach (var line in File.ReadAllLines(routinesFile, Encoding.UTF8))
                    {
                        var p = line.Split(new[] { '\t' }, 4);
                        if (p.Length < 4 || p[0] == "" || p[1].Length != 7 || p[1].IndexOf('1') < 0 || p[2].Length != 10) continue;
                        if (routines.Count >= MaxRoutines) break;
                        var r = new Routine { Id = p[0], Created = p[2], Text = p[3] };
                        for (int i = 0; i < 7; i++) r.Days[i] = p[1][i] == '1';
                        routines.Add(r);
                    }
            }
            catch { }
            try
            {
                if (File.Exists(routineLogFile))
                    foreach (var line in File.ReadAllLines(routineLogFile, Encoding.UTF8))
                    {
                        var p = line.Split('\t');
                        if (p.Length < 2 || p[1].Length != 10) continue;
                        List<string> l;
                        if (!routineLog.TryGetValue(p[0], out l)) { l = new List<string>(); routineLog[p[0]] = l; }
                        if (!l.Contains(p[1])) l.Add(p[1]);
                    }
            }
            catch { }
        }

        void SaveRoutines()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var r in routines)
                {
                    sb.Append(r.Id).Append('\t');
                    for (int i = 0; i < 7; i++) sb.Append(r.Days[i] ? '1' : '0');
                    sb.Append('\t').Append(r.Created).Append('\t').Append(OneLine(r.Text)).Append("\r\n");
                }
                WriteAtomic(routinesFile, sb.ToString());
            }
            catch (Exception ex) { flash = "SAVE FAILED: " + ex.Message; flashTimer.Start(); }
        }

        void SaveRoutineLog()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var r in routines)   // log lines for deleted routines are dropped
                {
                    List<string> l;
                    if (!routineLog.TryGetValue(r.Id, out l)) continue;
                    l.Sort(StringComparer.Ordinal);
                    if (l.Count > MaxRoutineLog) l.RemoveRange(0, l.Count - MaxRoutineLog);
                    foreach (var d in l) sb.Append(r.Id).Append('\t').Append(d).Append("\r\n");
                }
                WriteAtomic(routineLogFile, sb.ToString());
            }
            catch (Exception ex) { flash = "SAVE FAILED: " + ex.Message; flashTimer.Start(); }
        }

        void LoadVault()
        {
            Array.Clear(vault, 0, MaxVault);
            try
            {
                if (!File.Exists(vaultFile)) return;
                int n = 0;
                foreach (var line in File.ReadAllLines(vaultFile, Encoding.UTF8))
                {
                    var p = line.Split(new[] { '\t' }, 2);
                    if (p.Length < 2 || p[0].Length != 10 || p[1].Trim() == "") continue;
                    if (n >= MaxVault) break;
                    vault[n++] = new Idea { Added = p[0], Text = p[1] };
                }
            }
            catch { }
        }

        void SaveVault()
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var v in vault)
                    if (v != null) sb.Append(v.Added).Append('\t').Append(OneLine(v.Text)).Append("\r\n");
                WriteAtomic(vaultFile, sb.ToString());
            }
            catch (Exception ex) { flash = "SAVE FAILED: " + ex.Message; flashTimer.Start(); }
        }

        Dictionary<string, string> ReadSettings()
        {
            var m = new Dictionary<string, string>();
            try
            {
                if (File.Exists(settingsFile))
                    foreach (var line in File.ReadAllLines(settingsFile))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) m[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                    }
            }
            catch { }
            return m;
        }

        void LoadSettings()
        {
            var m = ReadSettings();
            string v;
            bool v2 = m.TryGetValue("v", out v) && v == "2";
            if (v2)
            {   // colors from v1 don't fit the new look, so only carry them over from v2 on
                if (m.TryGetValue("accent", out v)) { try { accent = Hex(v); } catch { } }
                if (m.TryGetValue("accentName", out v)) accentName = v;
                if (m.TryGetValue("dark", out v)) dark = v != "0";
            }
            if (m.TryGetValue("sound", out v)) sound = v != "0";
            if (m.TryGetValue("bootSound", out v)) bootSound = v != "0";
            if (m.TryGetValue("punchName", out v)) punchName = v;
            if (m.TryGetValue("bootName", out v)) bootName = v;
            customPunch = punchName != "" ? LoadWav(Path.Combine(dataDir, "punch.wav")) : null;
            customBoot = bootName != "" ? LoadWav(Path.Combine(dataDir, "boot.wav")) : null;
            if (customPunch == null) punchName = "";
            if (customBoot == null) bootName = "";
            if (m.TryGetValue("ui", out v)) uiSound = v != "0";
            if (m.TryGetValue("onTop", out v)) onTop = v == "1";
            if (m.TryGetValue("rotate", out v)) rotate = v == "1";
            if (m.TryGetValue("rotateShown", out v)) rotateShown = v;
        }

        void SaveSettings()
        {
            try
            {
                File.WriteAllLines(settingsFile, new[] {
                    "v=2",
                    "accent=" + ColorTranslator.ToHtml(accent),
                    "accentName=" + accentName,
                    "dark=" + (dark ? "1" : "0"),
                    "sound=" + (sound ? "1" : "0"),
                    "bootSound=" + (bootSound ? "1" : "0"),
                    "punchName=" + punchName,
                    "bootName=" + bootName,
                    "ui=" + (uiSound ? "1" : "0"),
                    "onTop=" + (onTop ? "1" : "0"),
                    "rotate=" + (rotate ? "1" : "0"),
                    "rotateShown=" + rotateShown,
                    "x=" + Left, "y=" + Top,
                });
            }
            catch { }
        }

        void RestorePosition()
        {
            var m = ReadSettings();
            string xs, ys; int x, y;
            if (m.TryGetValue("x", out xs) && m.TryGetValue("y", out ys) && int.TryParse(xs, out x) && int.TryParse(ys, out y))
                foreach (var sc in Screen.AllScreens)
                    if (sc.WorkingArea.IntersectsWith(new Rectangle(x + 40, y + 10, 100, 30)))
                    { StartPosition = FormStartPosition.Manual; Location = new Point(x, y); break; }
        }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        bool StartsWithWindows()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("OneCheck") != null; }
            catch { return false; }
        }
        void SetStartWithWindows(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("OneCheck", "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue("OneCheck", false);
                }
            }
            catch { }
        }
        // the Run entry stores the exe path; if the exe was moved or renamed, point it at this one
        void FixStartupPath()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    var v = k.GetValue("OneCheck") as string;
                    if (v == null) return;
                    if (!string.Equals(v.Trim().Trim('"'), Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                        k.SetValue("OneCheck", "\"" + Application.ExecutablePath + "\"");
                }
            }
            catch { }
        }

        // ---------- custom sounds ----------
        static byte[] LoadWav(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 8 * 1024 * 1024) return null;
                var b = File.ReadAllBytes(path);
                return Sfx.IsWav(b) ? b : null;
            }
            catch { return null; }
        }

        void PickSound(bool punch)
        {
            using (var dlg = new OpenFileDialog { Filter = "WAV audio (*.wav)|*.wav", Title = punch ? "Choose a punch sound" : "Choose a startup sound" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var b = LoadWav(dlg.FileName);
                if (b == null)
                {
                    flash = "WAV ONLY, UNDER 8 MB"; flashTimer.Stop(); flashTimer.Start(); Invalidate();
                    return;
                }
                try { File.WriteAllBytes(Path.Combine(dataDir, punch ? "punch.wav" : "boot.wav"), b); } catch { }
                string name = Path.GetFileName(dlg.FileName);
                if (punch) { customPunch = b; punchName = name; sound = true; }
                else { customBoot = b; bootName = name; bootSound = true; }
                SaveSettings();
                Sfx.Play(b, true);
                Invalidate();
            }
        }

        void ResetSound(bool punch)
        {
            if (punch) { customPunch = null; punchName = ""; } else { customBoot = null; bootName = ""; }
            try { File.Delete(Path.Combine(dataDir, punch ? "punch.wav" : "boot.wav")); } catch { }
            SaveSettings();
            Sfx.Play(punch ? Sfx.Punch : Sfx.Boot, true);
            Invalidate();
        }

        // ---------- actions ----------
        void CheckRollover()
        {
            string k = Key(DateTime.Today);
            if (k != todayKey) { todayKey = k; editing = false; stepScroll = 0; CancelStepAnim(); ApplyRotation(); AnnounceFaction(); Relayout(); }
        }

        // daily rotation: today's preset, by days since 2026-01-01 (the custom color is never part of it)
        void ApplyRotation()
        {
            if (!rotate) return;
            int n = Accents.Length;
            int i = (int)((DateTime.Today - new DateTime(2026, 1, 1)).TotalDays) % n;
            if (i < 0) i += n;
            accent = Accents[i].Color; accentName = Accents[i].Name;
        }

        // flashes today's faction once per day while rotation is on
        void AnnounceFaction()
        {
            string k = Key(DateTime.Today);
            if (!rotate || rotateShown == k) return;
            rotateShown = k;
            SaveSettings();
            flash = "FACTION // " + accentName.ToUpperInvariant();
            flashTimer.Stop(); flashTimer.Interval = 3500; flashTimer.Start();
            Invalidate();
        }

        void SetToday()
        {
            string v = txtToday.Text.Trim();
            if (v == "") return;
            var d = Get(todayKey); d.Task = v; days[todayKey] = d;
            editing = false; SaveDays(); ActiveControl = null;
            Sound(Sfx.Save);
            Relayout();
        }

        void SaveTomorrow()
        {
            string k = Key(DateTime.Today.AddDays(1));
            string v = txtTomorrow.Text.Trim();
            var d = Get(k);
            string old = d.Task;
            d.Task = v; days[k] = d;
            SaveDays();
            flash = v == "" ? "CLEARED" : "QUEUED";
            flashTimer.Stop(); flashTimer.Start();
            ActiveControl = null;
            Sound(Sfx.Save);
            if (v != "" && v != old) StartNextAnim();   // not for clearing or re-saving the same text
            Invalidate();
        }

        void AddStep()
        {
            string v = txtStep.Text.Trim();
            if (v == "") return;
            List<Step> l;
            if (!steps.TryGetValue(todayKey, out l)) { l = new List<Step>(); steps[todayKey] = l; }
            if (l.Count >= MaxSteps) return;
            l.Add(new Step { Text = v });
            SaveSteps();
            txtStep.Text = "";
            Sound(Sfx.Save);
            stepScroll = int.MaxValue;   // Relayout clamps this to the bottom, so the new step is in view
            CancelStepAnim();
            Relayout();
            if (txtStep.Visible) txtStep.Focus();
        }

        void ToggleStep(int i)
        {
            var l = Steps(todayKey);
            if (i < 0 || i >= l.Count || Get(todayKey).Done) return;
            l[i].Done = !l[i].Done;
            SaveSteps();
            bool last = StepsLeft() == 0 && RoutinesLeft() == 0 && l[i].Done;
            if (last) Sound(Sfx.Save); else Sound(Sfx.Click);
            CancelStepAnim();   // unchecking is instant, and re-locks the punch button
            if (l[i].Done)
            {   // stamp the row if it's on screen; the unlock waits for the stamp to finish
                bool visible = l.Count <= VisibleSteps || (i >= stepScroll && i < stepScroll + VisibleSteps);
                if (visible) { stepAnimIndex = i; stepAnimStart = DateTime.Now; }
                if (last) StartUnlock(visible ? StepAnimMs : 0f);
                if (visible || last) animTimer.Start();
            }
            Invalidate();
        }

        // progress of the step stamp / punch unlock, 1 = not running. UnlockAnim is negative while it waits its turn.
        float StepAnim
        {
            get { return stepAnimIndex < 0 ? 1f : Math.Min(1f, (float)(DateTime.Now - stepAnimStart).TotalMilliseconds / StepAnimMs); }
        }
        float NextAnim
        {
            get { return !nextAnimOn ? 1f : Math.Min(1f, (float)(DateTime.Now - nextAnimStart).TotalMilliseconds / NextAnimMs); }
        }
        // the NEXT text box sits on top of the field, so it's hidden (in Relayout) while the glitch plays
        void StartNextAnim()
        {
            nextAnimOn = true; nextAnimStart = DateTime.Now;
            if (txtTomorrow.Focused) ActiveControl = null;
            Relayout();
            animTimer.Start();
        }
        float UnlockAnim
        {
            get { return !unlockOn ? 1f : Math.Min(1f, (float)(DateTime.Now - unlockStart).TotalMilliseconds / UnlockMs); }
        }
        void StartUnlock(float delayMs) { unlockOn = true; unlockStart = DateTime.Now.AddMilliseconds(delayMs); }
        void CancelStepAnim() { stepAnimIndex = -1; unlockOn = false; }

        void DeleteStep(int i)
        {
            var l = Steps(todayKey);
            if (i < 0 || i >= l.Count || Get(todayKey).Done) return;
            CancelStepAnim();
            l.RemoveAt(i);
            if (l.Count == 0) steps.Remove(todayKey);
            SaveSteps();
            Sound(Sfx.Undo);
            Relayout();
        }

        int StepsLeft()
        {
            int n = 0;
            foreach (var st in Steps(todayKey)) if (!st.Done) n++;
            return n;
        }

        // ---------- routines ----------
        DateTime TodayDate { get { return DateTime.ParseExact(todayKey, "yyyy-MM-dd", CultureInfo.InvariantCulture); } }

        bool RoutineDoneOn(Routine r, string k) { List<string> l; return routineLog.TryGetValue(r.Id, out l) && l.Contains(k); }

        // "" = not listed today; otherwise TODAY, OVERDUE or DONE
        string RoutineTag(Routine r)
        {
            if (RoutineDoneOn(r, todayKey)) return "DONE";
            DateTime today = TodayDate, created;
            if (!DateTime.TryParseExact(r.Created, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out created)) created = today;
            // walk back to the most recent scheduled day in the last 7 days
            for (int i = 0; i < 7; i++)
            {
                DateTime d = today.AddDays(-i);
                if (d < created) return "";
                if (!r.Days[(int)d.DayOfWeek]) continue;
                if (i == 0) return "TODAY";
                string since = Key(d);   // yyyy-MM-dd strings sort like dates
                List<string> l;
                if (routineLog.TryGetValue(r.Id, out l))
                    foreach (var k in l) if (string.CompareOrdinal(k, since) >= 0) return "";
                return "OVERDUE";
            }
            return "";
        }

        List<Routine> DueRoutines()
        {
            var l = new List<Routine>();
            foreach (var r in routines) if (RoutineTag(r) != "") l.Add(r);
            return l;
        }

        int RoutinesLeft()
        {
            int n = 0;
            foreach (var r in routines) { string tag = RoutineTag(r); if (tag == "TODAY" || tag == "OVERDUE") n++; }
            return n;
        }

        void ToggleRoutine(int i)
        {
            var due = DueRoutines();
            if (i < 0 || i >= due.Count || Get(todayKey).Done) return;
            var r = due[i];
            List<string> l;
            if (!routineLog.TryGetValue(r.Id, out l)) { l = new List<string>(); routineLog[r.Id] = l; }
            bool nowDone = !l.Contains(todayKey);
            if (nowDone) l.Add(todayKey); else l.Remove(todayKey);
            SaveRoutineLog();
            bool last = nowDone && StepsLeft() == 0 && RoutinesLeft() == 0 && Get(todayKey).Task != "";
            if (last) Sound(Sfx.Save); else Sound(Sfx.Click);
            unlockOn = false;
            if (last) { StartUnlock(0f); animTimer.Start(); }
            Relayout();
        }

        void AddRoutine()
        {
            string v = txtRoutine.Text.Trim();
            if (v == "" || routines.Count >= MaxRoutines) return;
            var r = new Routine { Id = Guid.NewGuid().ToString("N").Substring(0, 8), Created = todayKey, Text = v };
            r.Days[(int)TodayDate.DayOfWeek] = true;
            routines.Add(r);
            SaveRoutines();
            txtRoutine.Text = "";
            Sound(Sfx.Save);
            Relayout();
            if (txtRoutine.Visible) txtRoutine.Focus();
        }

        void DeleteRoutine(int i)
        {
            if (i < 0 || i >= routines.Count) return;
            routineLog.Remove(routines[i].Id);
            routines.RemoveAt(i);
            SaveRoutines();
            SaveRoutineLog();
            Sound(Sfx.Undo);
            Relayout();
        }

        void ToggleRoutineDay(int i, int d)
        {
            if (i < 0 || i >= routines.Count || d < 0 || d > 6) return;
            var r = routines[i];
            if (r.Days[d])
            {
                int on = 0;
                foreach (bool b in r.Days) if (b) on++;
                if (on <= 1) { flash = "ONE DAY MINIMUM"; flashTimer.Stop(); flashTimer.Start(); Invalidate(); return; }
            }
            r.Days[d] = !r.Days[d];
            SaveRoutines();
            Relayout();
        }

        // ---------- vault ----------
        // Up to MaxVault big ideas. They can only leave by being queued as tomorrow's task or deleted;
        // they never touch today's task, the steps, the routines or the streak.
        int VaultCount() { int n = 0; foreach (var v in vault) if (v != null) n++; return n; }

        // tomorrow is taken if a task is queued, or one is typed in the NEXT box but not queued yet
        bool NextTaken
        {
            get { return Get(Key(DateTime.Today.AddDays(1))).Task != "" || txtTomorrow.Text.Trim() != ""; }
        }

        static int VaultAge(Idea v)
        {
            DateTime a;
            if (!DateTime.TryParseExact(v.Added, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out a)) return 0;
            return Math.Max(0, (DateTime.Today - a).Days);
        }

        // starting an animation on a slot replaces whatever was running there
        void StartVaultAnim(int i, int kind, Idea ghost)
        {
            vAnimKind[i] = kind; vAnimStart[i] = DateTime.Now; vGhost[i] = ghost;
            animTimer.Start();
        }

        // running animation on a slot (VNone if idle or finished) and its progress 0..1
        int VaultAnim(int i, out float p)
        {
            p = 1f;
            int kind = vAnimKind[i];
            if (kind == VNone) return VNone;
            float dur = kind == VStore ? VStoreMs : kind == VMove ? VMoveMs : VDeleteMs;
            p = Math.Max(0f, Math.Min(1f, (float)(DateTime.Now - vAnimStart[i]).TotalMilliseconds / dur));
            return p < 1f ? kind : VNone;
        }

        // drops finished vault animations; true while any is still running
        bool VaultAnimTick()
        {
            bool any = false;
            for (int i = 0; i < MaxVault; i++)
            {
                float p;
                if (vAnimKind[i] == VNone) continue;
                if (VaultAnim(i, out p) == VNone) { vAnimKind[i] = VNone; vGhost[i] = null; }
                else any = true;
            }
            return any;
        }

        void ClearVaultConfirm() { vConfirm = -1; confirmTimer.Stop(); }

        void AddVault()
        {
            string v = txtVault.Text.Trim();
            if (v == "") return;
            int i = Array.IndexOf(vault, null);
            if (i < 0) return;
            vault[i] = new Idea { Added = Key(DateTime.Today), Text = v };
            SaveVault();
            txtVault.Text = "";
            ClearVaultConfirm();
            Sound(Sfx.Save);
            StartVaultAnim(i, VStore, null);
            Relayout();
            if (txtVault.Visible) txtVault.Focus(); else ActiveControl = null;
        }

        // the only way out of the vault besides deleting: become tomorrow's task, and only if tomorrow is free
        void MoveVault(int i)
        {
            if (i < 0 || i >= MaxVault || vault[i] == null) return;
            if (NextTaken) { Sound(Sfx.Undo); return; }
            var idea = vault[i];
            string k = Key(DateTime.Today.AddDays(1));
            var d = Get(k); d.Task = idea.Text; days[k] = d;
            SaveDays();
            txtTomorrow.Text = idea.Text;
            vault[i] = null;
            SaveVault();
            ClearVaultConfirm();
            flash = "QUEUED";
            flashTimer.Stop(); flashTimer.Start();
            Sound(Sfx.Save);
            StartNextAnim();
            StartVaultAnim(i, VMove, idea);
            Relayout();
        }

        // first click arms the row ("SURE?") for a few seconds, the second one deletes
        void DeleteVault(int i)
        {
            if (i < 0 || i >= MaxVault || vault[i] == null) return;
            if (vConfirm != i)
            {
                vConfirm = i;
                confirmTimer.Stop(); confirmTimer.Start();
                Sound(Sfx.Click);
                Invalidate();
                return;
            }
            var idea = vault[i];
            vault[i] = null;
            SaveVault();
            ClearVaultConfirm();
            Sound(Sfx.Undo);
            StartVaultAnim(i, VDelete, idea);
            Relayout();
        }

        void Punch()
        {
            var d = Get(todayKey);
            if (d.Task == "" || d.Done) return;
            // locked until every step and every listed routine is checked
            if (StepsLeft() > 0 || RoutinesLeft() > 0) { Sound(Sfx.Undo); return; }
            d.Done = true; d.DoneAt = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            days[todayKey] = d; SaveDays();
            if (sound) Sfx.Play(customPunch ?? Sfx.Punch, true);
            CancelStepAnim();
            punchAnim = 0f; punchStart = DateTime.Now; animTimer.Start();
            Relayout();
        }

        void Unpunch()
        {
            var d = Get(todayKey); d.Done = false; d.DoneAt = ""; days[todayKey] = d; SaveDays();
            Sound(Sfx.Undo);
            Relayout();
        }

        int Streak()
        {
            DateTime d = DateTime.Today; int n = 0;
            if (!Get(Key(d)).Done) d = d.AddDays(-1);
            while (Get(Key(d)).Done) { n++; d = d.AddDays(-1); }
            return n;
        }

        // ---------- layout ----------
        // The today panel grows with the step list (up to VisibleSteps rows, then the list scrolls);
        // everything below it shifts down.
        const int TY = 138;          // top of the today panel
        const int RowsTop = 110;     // first step row, relative to TY
        const int RowH = 28;
        const int SbW = 10;          // strip kept clear on the right of the rows for the scrollbar

        bool TaskSet { get { return Get(todayKey).Task != "" && !editing; } }
        bool AddVisible { get { return TaskSet && !Get(todayKey).Done && Steps(todayKey).Count < MaxSteps; } }
        int StepRows { get { return Math.Min(Steps(todayKey).Count, VisibleSteps); } }
        int AddY { get { return RowsTop + RowH * StepRows + 2; } }
        int PunchY { get { return AddVisible ? AddY + 32 + 14 : RowsTop + RowH * StepRows + 10; } }

        // steps list scrolling
        bool StepsScroll { get { return !showSettings && TaskSet && Steps(todayKey).Count > VisibleSteps; } }
        Rectangle StepsArea { get { return R(Pad + 14, TY + RowsTop, LW - 2 * Pad - 28, RowH * StepRows); } }
        Rectangle StepTrack { get { return R(LW - Pad - 14 - 4, TY + RowsTop, 4, RowH * VisibleSteps); } }
        Rectangle StepThumb
        {
            get
            {
                var tk = StepTrack;
                int n = Steps(todayKey).Count, max = Math.Max(1, n - VisibleSteps);
                int h = Math.Min(tk.Height, Math.Max(D(16), tk.Height * VisibleSteps / Math.Max(1, n)));
                return new Rectangle(tk.X, tk.Y + (tk.Height - h) * Math.Min(stepScroll, max) / max, tk.Width, h);
            }
        }

        void ClampScroll()
        {
            stepScroll = Math.Max(0, Math.Min(stepScroll, Math.Max(0, Steps(todayKey).Count - VisibleSteps)));
        }

        void ScrollTo(int v)
        {
            int old = stepScroll;
            stepScroll = v;
            ClampScroll();
            if (stepScroll == old) return;
            Invalidate();
            Update();   // repaint now so the hit rects match the new rows, then re-pick what's under the cursor
            string h = HitAt(PointToClient(Cursor.Position));
            if (h != hover) { hover = h; Cursor = h == "" ? Cursors.Default : Cursors.Hand; Invalidate(); }
        }

        // WM_MOUSEWHEEL goes to whichever control has focus (usually a text box), so catch it here instead
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != 0x020A || !StepsScroll) return false;
            Control c = Control.FromHandle(m.HWnd);
            if (c == null || (c != this && c.FindForm() != this)) return false;
            if (!StepsArea.Contains(PointToClient(Cursor.Position))) return false;
            wheelAcc += (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            int notches = wheelAcc / 120;
            wheelAcc -= notches * 120;
            if (notches != 0) ScrollTo(stepScroll - notches);
            return true;
        }
        int TodayH { get { return TaskSet ? PunchY + 60 + (Get(todayKey).Done ? 32 : 16) : 210; } }
        // routine panel sits under today and only takes space when something is listed
        const int RtRowH = 26;
        int RtY { get { return TY + TodayH + 12; } }
        int RtH { get { int n = DueRoutines().Count; return n == 0 ? 0 : 30 + RtRowH * n + 8; } }
        int NextY { get { int h = RtH; return RtY + (h > 0 ? h + 12 : 0); } }
        // vault panel: fixed height, always MaxVault slot rows plus the add row
        const int VRowsTop = 30, VRowH = 26;
        const int VAddY = VRowsTop + VRowH * MaxVault + 6;
        const int VaultH = VAddY + 30 + 12;
        int VaultY { get { return NextY + 114; } }
        int LogY { get { return VaultY + VaultH + 12; } }
        int MainH { get { return LogY + 224; } }

        // config + routines screens
        const int SettingsH = 780;
        const int RRowTop = 138 + 34, RRowH = 52;
        int RListH { get { return routines.Count == 0 ? 26 : RRowH * routines.Count; } }
        int RFieldY { get { return RRowTop + RListH + 6; } }
        int RPanelH { get { return 34 + RListH + (routines.Count < MaxRoutines ? 6 + 36 + 14 : 26); } }
        int RBackY { get { return 138 + RPanelH + 12; } }
        int RoutinesH { get { return RBackY + 52 + 22; } }

        Rectangle PanToday { get { return R(Pad, TY, LW - 2 * Pad, TodayH); } }
        Rectangle PanRoutine { get { return R(Pad, RtY, LW - 2 * Pad, RtH); } }
        Rectangle PanNext { get { return R(Pad, NextY, LW - 2 * Pad, 102); } }
        Rectangle PanVault { get { return R(Pad, VaultY, LW - 2 * Pad, VaultH); } }
        Rectangle PanLog { get { return R(Pad, LogY, LW - 2 * Pad, 198); } }

        void Relayout()
        {
            var t = Get(todayKey);
            ClampScroll();
            bool booting = bootAnim < 1f;
            bool todayBox = !showSettings && !booting && (t.Task == "" || editing);
            fieldToday = R(Pad + 16, 138 + 46, LW - 2 * Pad - 32 - 88, 42);
            fieldTomorrow = R(Pad + 16, NextY + 42, LW - 2 * Pad - 32 - 88, 42);
            fieldStep = R(Pad + 14, TY + AddY, LW - 2 * Pad - 28, 32);
            fieldRoutine = R(Pad + 14, RFieldY, LW - 2 * Pad - 28 - 80 - 6, 36);
            fieldVault = R(Pad + 14, VaultY + VAddY, LW - 2 * Pad - 28 - 64 - 6, 30);
            // config screens never shrink the window below the main screen, so switching doesn't jump
            int need = showSettings ? Math.Max(MainH, showRoutines ? RoutinesH : SettingsH) : MainH;
            int h = D(Math.Max(need, LH));
            if (ClientSize.Height != h) ClientSize = new Size(ClientSize.Width, h);
            PlaceBox(txtToday, fieldToday, todayBox);
            PlaceBox(txtTomorrow, fieldTomorrow, !showSettings && !booting && !nextAnimOn);
            PlaceBox(txtStep, fieldStep, !showSettings && !booting && AddVisible);
            PlaceBox(txtRoutine, fieldRoutine, showSettings && showRoutines && !booting && routines.Count < MaxRoutines);
            PlaceBox(txtVault, fieldVault, !showSettings && !booting && VaultCount() < MaxVault);
            if (todayBox && !txtToday.Focused) txtToday.Text = t.Task;
            if (!txtTomorrow.Focused) txtTomorrow.Text = Get(Key(DateTime.Today.AddDays(1))).Task;
            if (todayBox && editing) { txtToday.Focus(); txtToday.SelectAll(); }
            Invalidate();
        }

        void PlaceBox(TextBox tb, Rectangle field, bool visible)
        {
            tb.Visible = visible;
            int h = tb.PreferredHeight;
            tb.SetBounds(field.X + D(12), field.Y + (field.Height - h) / 2 + D(1), field.Width - D(24), h);
        }

        // ---------- drawing primitives ----------
        void FillR(Graphics g, Rectangle r, Color c) { using (var b = new SolidBrush(c)) g.FillRectangle(b, r); }
        void LineR(Graphics g, Rectangle r, Color c, float w)
        {
            using (var p = new Pen(c, w) { Alignment = PenAlignment.Inset }) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        void TextIn(Graphics g, string s, Font f, Color c, Rectangle r, StringAlignment h, StringAlignment v)
        {
            using (var b = new SolidBrush(c))
            using (var sf = new StringFormat { Alignment = h, LineAlignment = v, Trimming = StringTrimming.EllipsisWord })
                g.DrawString(s, f, b, r, sf);
        }

        SizeF Measure(Graphics g, string s, Font f) { return g.MeasureString(s, f, 2000, StringFormat.GenericTypographic); }

        // wide display type: stretched horizontally
        float Wide(Graphics g, string s, Font f, Color c, float x, float y, float sx)
        {
            var st = g.Save();
            g.TranslateTransform(x, y);
            g.ScaleTransform(sx, 1f);
            // ClearType only smooths horizontally, which leaves big stretched type jagged; use grayscale here
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, 0, 0, StringFormat.GenericTypographic);
            g.Restore(st);   // also puts the text rendering hint back
            return Measure(g, s, f).Width * sx;
        }
        float WideW(Graphics g, string s, Font f, float sx) { return Measure(g, s, f).Width * sx; }
        void WideCenterV(Graphics g, string s, Font f, Color c, float x, Rectangle r, float sx)
        {
            float h = Measure(g, s, f).Height;
            Wide(g, s, f, c, x, r.Y + (r.Height - h) / 2f, sx);
        }

        // tiny technical label, tracked out
        float Micro(Graphics g, string s, float x, float y, Color c, bool bold)
        {
            Font f = bold ? fMicroB : fMicro;
            // whole-pixel origin and advances, so no glyph lands on a half pixel
            x = (float)Math.Round(x); y = (float)Math.Round(y);
            float cx = x;
            using (var b = new SolidBrush(c))
                foreach (char ch in s.ToUpperInvariant())
                {
                    string cs = ch == ' ' ? "i" : ch.ToString();
                    if (ch != ' ') g.DrawString(cs, f, b, cx, y, StringFormat.GenericTypographic);
                    cx += MicroAdv(g, cs, f);
                }
            return cx - x;
        }
        float MicroW(Graphics g, string s, bool bold)
        {
            Font f = bold ? fMicroB : fMicro; float w = 0;
            foreach (char ch in s.ToUpperInvariant()) w += MicroAdv(g, ch == ' ' ? "i" : ch.ToString(), f);
            return w;
        }
        // one character's advance incl. tracking, rounded so Micro and MicroW agree
        float MicroAdv(Graphics g, string cs, Font f) { return (float)Math.Round(Measure(g, cs, f).Width + 1.1f * S); }

        // corner registration brackets
        void Brackets(Graphics g, Rectangle r, Color c, float len)
        {
            float l = len * S;
            using (var p = new Pen(c, 1.5f * S))
            {
                float x0 = r.X, y0 = r.Y, x1 = r.Right - 1, y1 = r.Bottom - 1;
                g.DrawLine(p, x0, y0, x0 + l, y0); g.DrawLine(p, x0, y0, x0, y0 + l);
                g.DrawLine(p, x1, y0, x1 - l, y0); g.DrawLine(p, x1, y0, x1, y0 + l);
                g.DrawLine(p, x0, y1, x0 + l, y1); g.DrawLine(p, x0, y1, x0, y1 - l);
                g.DrawLine(p, x1, y1, x1 - l, y1); g.DrawLine(p, x1, y1, x1, y1 - l);
            }
        }

        void Hazard(Graphics g, Rectangle r, Color stripe, float step)
        {
            var st = g.Save();
            g.SetClip(r, CombineMode.Intersect);   // stays inside whatever clip the caller already set
            float s = step * S;
            using (var b = new SolidBrush(stripe))
                for (float x = r.X - r.Height; x < r.Right + r.Height; x += s * 2)
                    g.FillPolygon(b, new[] {
                        new PointF(x, r.Bottom), new PointF(x + s, r.Bottom),
                        new PointF(x + s + r.Height, r.Y), new PointF(x + r.Height, r.Y) });
            g.Restore(st);
        }

        void Barcode(Graphics g, Rectangle r, int seed, Color c)
        {
            var rnd = new Random(seed);
            float x = r.X;
            using (var b = new SolidBrush(c))
                while (x < r.Right)
                {
                    float w = (1 + rnd.Next(3)) * S;
                    if (x + w > r.Right) break;
                    g.FillRectangle(b, x, r.Y, w, r.Height);
                    x += w + (1 + rnd.Next(3)) * S;
                }
        }

        void Chevrons(Graphics g, float x, float cy, Color c, int n)
        {
            float h = 6 * S, w = 4 * S;
            using (var p = new Pen(c, 2f * S) { StartCap = LineCap.Square, EndCap = LineCap.Square, LineJoin = LineJoin.Miter })
                for (int i = 0; i < n; i++)
                {
                    float xx = x + i * 7 * S;
                    g.DrawLines(p, new[] { new PointF(xx, cy - h), new PointF(xx + w, cy), new PointF(xx, cy + h) });
                }
        }

        void Hit(string k, Rectangle r) { hits[k] = r; }
        bool Hov(string k) { return hover == k; }

        // panel = flat fill + hairline + corner brackets + header row
        void Panel(Graphics g, Rectangle r, string code, string title)
        {
            FillR(g, r, cPanel);
            LineR(g, r, cLine, 1f);
            Brackets(g, r, cInk, 7);
            float x = r.X + D(14), y = r.Y + D(12);
            float w = Micro(g, code, x, y, AccentText, true);
            Micro(g, "// " + title, x + w + D(6), y, cMuted, false);
        }

        // ---------- painting ----------
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            hits.Clear();

            FillR(g, ClientRectangle, cBg);
            PaintTitleBar(g);
            if (showSettings) { if (showRoutines) PaintRoutines(g); else PaintSettings(g); }
            else PaintMain(g);
            if (bootAnim < 1f) PaintBootWipe(g);

            using (var pen = new Pen(cLine, 1)) g.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
        }

        void PaintBootWipe(Graphics g)
        {
            // each band reveals left-to-right, staggered top to bottom, with an accent scanline at the edge
            int[] bands = { 38, 136, NextY - 2, VaultY - 2, LogY - 2, LogY + 204, Math.Max(MainH, LH) };
            for (int i = 0; i < bands.Length - 1; i++)
            {
                float p = Math.Max(0f, Math.Min(1f, (bootAnim - i * 0.1f) / 0.5f));
                p = 1 - (float)Math.Pow(1 - p, 3);
                int y0 = D(bands[i]), y1 = D(bands[i + 1]);
                int x = (int)(ClientSize.Width * p);
                if (p < 1f) FillR(g, new Rectangle(x, y0, ClientSize.Width - x, y1 - y0), cBg);
                if (p > 0f && p < 1f) FillR(g, new Rectangle(x - D(3), y0, D(3), y1 - y0), accent);
            }
        }

        void PaintTitleBar(Graphics g)
        {
            FillR(g, R(Pad, 14, 10, 10), accent);
            // keep the title clear of the window buttons: drop the micro label first, then ease the stretch, then shrink
            const string title = "TODAY'S CONTRACTS";
            float x0 = D(Pad + 18), room = D(LW - 114 - 10) - x0;
            Font f = fMid;
            float sx = WIDE, w = WideW(g, title, f, sx);
            bool label = w + D(8) + Measure(g, "SYS.02", fMicro).Width <= room;
            if (w > room)
            {
                sx = Math.Max(1f, WIDE * room / w);
                w = WideW(g, title, f, sx);
                if (w > room)
                {
                    float size = Math.Max(7f, (float)Math.Floor(fMid.Size * room / w * 2f) / 2f);
                    if (fTitleFit == null || fTitleFitSize != size)
                    {
                        if (fTitleFit != null) fTitleFit.Dispose();
                        fTitleFit = new Font(fMid.FontFamily, size, fMid.Style);
                        fTitleFitSize = size;
                    }
                    f = fTitleFit;
                }
            }
            w = Wide(g, title, f, cInk, x0, D(11) + (fMid.Height - f.Height) / 2f, sx);
            if (label) Micro(g, "SYS.02", x0 + w + D(8), D(16), cMuted, false);

            string[] keys = { "gear", "min", "close" };
            for (int i = 0; i < 3; i++)
            {
                var r = R(LW - 114 + i * 38, 0, 38, 36);
                Hit(keys[i], r);
                bool hv = Hov(keys[i]);
                if (hv) FillR(g, r, keys[i] == "close" ? Hex("#FF3B30") : accent);
                bool active = keys[i] == "gear" && showSettings;
                Color c = hv ? OnColor(keys[i] == "close" ? Hex("#FF3B30") : accent) : (active ? AccentText : cMuted);
                DrawIcon(g, keys[i], r, c);
            }
            using (var p = new Pen(cLine, 1)) g.DrawLine(p, 0, D(36), ClientSize.Width, D(36));
        }

        void DrawIcon(Graphics g, string k, Rectangle r, Color c)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = 4.5f * S;
            using (var pen = new Pen(c, 1.6f * S) { StartCap = LineCap.Square, EndCap = LineCap.Square })
            {
                if (k == "close") { g.DrawLine(pen, cx - u, cy - u, cx + u, cy + u); g.DrawLine(pen, cx - u, cy + u, cx + u, cy - u); }
                else if (k == "min") g.DrawLine(pen, cx - u, cy + u * 0.4f, cx + u, cy + u * 0.4f);
                else
                {   // config: three bars of different lengths
                    g.DrawLine(pen, cx - u, cy - u, cx + u, cy - u);
                    g.DrawLine(pen, cx - u, cy, cx + u * 0.3f, cy);
                    g.DrawLine(pen, cx - u, cy + u, cx + u * 0.9f, cy + u);
                }
            }
        }

        // cached shrunk header font; size is rounded down to 0.25pt so it's only rebuilt when the day changes
        Font HugeFit(float size)
        {
            size = Math.Max(8f, (float)Math.Floor(size * 4) / 4f);
            if (fHugeFit == null || fHugeFitSize != size)
            {
                if (fHugeFit != null) fHugeFit.Dispose();
                fHugeFit = new Font(fHuge.FontFamily, size, fHuge.Style);
                fHugeFitSize = size;
            }
            return fHugeFit;
        }

        void PaintMain(Graphics g)
        {
            var now = DateTime.Now;
            var t = Get(todayKey);
            int streak = Streak();
            bool glitch = punchAnim < 1f;

            // ---- header ----
            string dayName = now.ToString("dddd", CultureInfo.CurrentCulture).ToUpperInvariant();
            float maxW = D(LW - Pad - 104 - 10) - D(Pad - 2);
            float w = WideW(g, dayName, fHuge, WIDE);
            if (w > maxW)
            {   // too wide for the space left of the streak box: draw a smaller copy on the same baseline
                Font f = HugeFit(fHuge.SizeInPoints * maxW / w);
                var ff = fHuge.FontFamily;
                float ascent = ff.GetCellAscent(fHuge.Style) / (float)ff.GetEmHeight(fHuge.Style) * g.DpiY / 72f;
                Wide(g, dayName, f, cInk, D(Pad - 2), D(46) + (fHuge.SizeInPoints - f.SizeInPoints) * ascent, WIDE);
            }
            else Wide(g, dayName, fHuge, cInk, D(Pad - 2), D(46), WIDE);
            int week = CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(now, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
            string meta = now.ToString("MM.dd.yyyy", CultureInfo.InvariantCulture) + "  //  WK " + week.ToString("00") + "  //  D" + now.DayOfYear.ToString("000");
            Micro(g, meta, D(Pad), D(96), cMuted, false);

            // streak block
            var sb = R(LW - Pad - 104, 46, 104, 66);
            bool flick = glitch && (frame / 3) % 2 == 0;
            Color sbFill = streak > 0 ? accent : cHole;
            if (flick) sbFill = cInk;
            FillR(g, sb, sbFill);
            Color sbInk = streak > 0 || flick ? OnColor(sbFill) : cMuted;
            Micro(g, "STREAK", sb.X + D(8), sb.Y + D(7), sbInk, true);
            string num = streak.ToString("00");
            float nw = WideW(g, num, fHuge, WIDE);
            Wide(g, num, fHuge, sbInk, sb.Right - D(8) - nw, sb.Y + D(18), WIDE);

            // hazard strip under the header
            var hz = R(Pad, 118, LW - 2 * Pad, 8);
            FillR(g, hz, cHole);
            Hazard(g, hz, cDim, 5);
            FillR(g, new Rectangle(hz.X, hz.Y, D(60), hz.Height), accent);

            // ---- today ----
            var c = PanToday;
            Panel(g, c, "[01]", "TODAY");
            Barcode(g, new Rectangle(c.Right - D(14) - D(72), c.Y + D(12), D(72), D(9)), DateTime.Today.DayOfYear * 31, cMuted);

            if (t.Task == "" || editing)
            {
                PaintField(g, fieldToday, txtToday.Focused);
                var set = R(LW - Pad - 16 - 80, 138 + 46, 80, 42);
                Hit("setToday", set);
                PaintSolidBtn(g, set, "SET", Hov("setToday"));
                string hint = editing ? "ENTER TO SAVE  //  ESC TO CANCEL" : "ONE THING YOU CAN FINISH TODAY. SMALL IS FINE.";
                Micro(g, hint, fieldToday.X, fieldToday.Bottom + D(14), cMuted, false);
            }
            else
            {
                var tr = R(Pad + 16, TY + 34, LW - 2 * Pad - 32, 58);
                if (!t.Done) Hit("editTask", tr);
                Font tf = g.MeasureString(t.Task, fTask, tr.Width).Height > tr.Height ? fTaskSmall : fTask;
                if (Hov("editTask"))
                    using (var pen = new Pen(cDim, 1) { DashStyle = DashStyle.Dash })
                        g.DrawRectangle(pen, tr.X - D(5), tr.Y - D(3), tr.Width + D(10), tr.Height + D(4));
                TextIn(g, t.Task, tf, t.Done ? cMuted : cInk, tr, StringAlignment.Near, StringAlignment.Near);

                PaintSteps(g, t);
                if (!t.Done) PaintPunchButton(g);
                else PaintComplete(g, t, streak);
            }

            // ---- routines (only when something is listed) ----
            var due = DueRoutines();
            if (due.Count > 0) PaintRoutinePanel(g, due, t);

            // ---- next ----
            c = PanNext;
            Panel(g, c, "[02]", "NEXT");
            if (flash != "")
            {
                float fw = MicroW(g, flash, true);
                var fr = new Rectangle((int)(c.Right - D(14) - fw - D(8)), c.Y + D(9), (int)(fw + D(8)), D(14));
                FillR(g, fr, accent);
                Micro(g, flash, fr.X + D(4), fr.Y + D(2), OnColor(accent), true);
            }
            if (nextAnimOn) PaintNextAnim(g);
            else PaintField(g, fieldTomorrow, txtTomorrow.Focused);
            var sv = R(LW - Pad - 16 - 80, NextY + 42, 80, 42);
            Hit("saveTomorrow", sv);
            bool hasTomorrow = Get(Key(DateTime.Today.AddDays(1))).Task != "";
            PaintOutlineBtn(g, sv, hasTomorrow ? "UPDATE" : "QUEUE", Hov("saveTomorrow"));

            // ---- vault ----
            PaintVault(g);

            // ---- log grid ----
            c = PanLog;
            Panel(g, c, "[04]", "LOG  //  28 CYCLES");
            DateTime today = DateTime.Today;
            DateTime end = today.AddDays(6 - (int)today.DayOfWeek);
            DateTime start = end.AddDays(-27);
            int doneCount = 0;
            for (int i = 0; i < 28; i++) if (Get(Key(start.AddDays(i))).Done) doneCount++;
            string cnt = doneCount.ToString("00") + "/28";
            Micro(g, cnt, c.Right - D(14) - MicroW(g, cnt, true), c.Y + D(12), AccentText, true);

            float gx = c.X + D(14), gw = c.Width - D(28), cell = gw / 7f;
            string[] dows = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };
            for (int i = 0; i < 7; i++)
            {
                float mw = MicroW(g, dows[i], false);
                Micro(g, dows[i], gx + i * cell + (cell - mw) / 2f, c.Y + D(34), cMuted, false);
            }
            float sz = D(32), rowH = D(36);
            for (int i = 0; i < 28; i++)
            {
                DateTime d = start.AddDays(i);
                int col = i % 7, row = i / 7;
                float cx = gx + col * cell + cell / 2, cy = c.Y + D(50) + row * rowH + rowH / 2;
                var cr = new Rectangle((int)(cx - sz / 2), (int)(cy - sz / 2), (int)sz, (int)sz);
                var day = Get(Key(d));
                bool future = d > today, isToday = d == today;
                string dn = d.Day.ToString("00");
                if (future)
                {
                    using (var pen = new Pen(cLine, 1f) { DashStyle = DashStyle.Dot }) g.DrawRectangle(pen, cr.X, cr.Y, cr.Width - 1, cr.Height - 1);
                    TextIn(g, dn, fCell, cDim, cr, StringAlignment.Center, StringAlignment.Center);
                }
                else if (day.Done)
                {
                    bool fresh = isToday && glitch;
                    FillR(g, cr, fresh && (frame / 2) % 2 == 0 ? cInk : accent);
                    TextIn(g, dn, fCell, OnColor(accent), cr, StringAlignment.Center, StringAlignment.Center);
                }
                else
                {
                    FillR(g, cr, cHole);
                    TextIn(g, dn, fCell, cMuted, cr, StringAlignment.Center, StringAlignment.Center);
                }
                if (isToday && !day.Done)
                {
                    var tl = Steps(Key(d));
                    if (tl.Count > 0)
                    {
                        int dn2 = 0; foreach (var st in tl) if (st.Done) dn2++;
                        FillR(g, new Rectangle(cr.X, cr.Bottom - D(4), cr.Width * dn2 / tl.Count, D(4)), accent);
                    }
                }
                if (isToday)
                {
                    var o = Rectangle.Inflate(cr, D(3), D(3));
                    using (var pen = new Pen(cInk, 1.5f * S)) g.DrawRectangle(pen, o);
                }
            }

            // ---- footer ----
            Micro(g, "Remember: Inefficiency is loss", D(Pad), D(LogY + 208), cMuted, true);
            Barcode(g, R(LW - Pad - 90, LogY + 206, 90, 9), 77, cDim);
        }

        void PaintSteps(Graphics g, Day t)
        {
            var l = Steps(todayKey);
            int n = l.Count, done = 0;
            foreach (var st in l) if (st.Done) done++;
            float x = D(Pad + 14), y = D(TY + 96);
            float w = Micro(g, "STEPS", x, y, AccentText, true);
            Micro(g, n == 0 ? "// BREAK IT INTO PIECES" : "// " + done.ToString("00") + "/" + n.ToString("00"), x + w + D(6), y, cMuted, false);
            // progress on the right: one segment per step, or a single thin bar once they'd no longer fit
            // a step that was just checked gets a short stamp animation (see ToggleStep); ai = its index, ms = time in
            float sa = StepAnim, ms = sa * StepAnimMs;
            int ai = sa < 1f ? stepAnimIndex : -1;
            bool segFlash = ai >= 0 && ms < 60f;   // its progress segment blinks ink before settling to accent
            float segW = D(12), segG = D(3), right = D(LW - Pad - 14);
            if (n > 10)
            {
                int bw = (int)(10 * (segW + segG) - segG);
                var bar = new Rectangle((int)right - bw, (int)y + D(2), bw, D(4));
                FillR(g, bar, cHole);
                FillR(g, new Rectangle(bar.X, bar.Y, bar.Width * done / n, bar.Height), segFlash ? cInk : accent);
            }
            else for (int i = 0; i < n; i++)
            {
                float sx = right - (n - i) * (segW + segG) + segG;
                FillR(g, new Rectangle((int)sx, (int)y + D(1), (int)segW, D(6)), !l[i].Done ? cHole : segFlash && i == ai ? cInk : accent);
            }

            // rows: only the visible window is drawn (and hit-tested), each in its slot
            ClampScroll();
            bool scroll = n > VisibleSteps;
            int first = scroll ? stepScroll : 0, last = Math.Min(n, first + VisibleSteps);
            int above = first, below = n - last;
            if (scroll) Hit("sbar", R(LW - Pad - 14 - SbW, TY + RowsTop, SbW, RowH * VisibleSteps));
            var clip = g.Save();
            g.SetClip(StepsArea);
            for (int i = first; i < last; i++)
            {
                int slot = i - first;
                var rr = R(Pad + 14, TY + RowsTop + RowH * slot, LW - 2 * Pad - 28 - (scroll ? SbW : 0), RowH);
                var del = new Rectangle(rr.Right - D(26), rr.Y + D(2), D(24), rr.Height - D(4));
                bool rowHov = !t.Done && (Hov("step" + i) || Hov("del" + i));
                if (!t.Done) { Hit("del" + i, del); Hit("step" + i, rr); }
                if (rowHov) FillR(g, rr, cHole);

                // stamp animation: a scanline sweeps the row left to right, leaving a tint that fades out
                bool anim = i == ai;
                float sweepX = rr.Right;
                if (anim)
                {
                    float sp = Math.Min(1f, ms / 280f);
                    sp = 1 - (1 - sp) * (1 - sp);
                    sweepX = rr.X + rr.Width * sp;
                    FillR(g, new Rectangle(rr.X, rr.Y, (int)(sweepX - rr.X), rr.Height), Color.FromArgb((int)(46 * (1 - sa)), accent));
                }

                var box = new Rectangle(rr.X + D(6), rr.Y + (rr.Height - D(16)) / 2, D(16), D(16));
                if (l[i].Done)
                {
                    var bst = g.Save();
                    if (anim)
                    {   // the box lands oversized and snaps down to size
                        float bp = Math.Min(1f, ms / 200f);
                        float sc = 1.5f - 0.5f * (1 - (float)Math.Pow(1 - bp, 3));
                        float bcx = box.X + box.Width / 2f, bcy = box.Y + box.Height / 2f;
                        g.TranslateTransform(bcx, bcy);
                        g.ScaleTransform(sc, sc);
                        g.TranslateTransform(-bcx, -bcy);
                    }
                    FillR(g, box, accent);
                    if (!anim || ms >= 80f)
                        using (var p = new Pen(OnColor(accent), 2f * S) { StartCap = LineCap.Square, EndCap = LineCap.Square })
                            g.DrawLines(p, new[] { new PointF(box.X + 3.5f * S, box.Y + 8f * S), new PointF(box.X + 6.5f * S, box.Y + 11.5f * S), new PointF(box.X + 12.5f * S, box.Y + 4.5f * S) });
                    g.Restore(bst);
                }
                else using (var p = new Pen(cInk, 1.5f * S)) g.DrawRectangle(p, box.X, box.Y, box.Width - 1, box.Height - 1);

                var tr = new Rectangle(box.Right + D(10), rr.Y, rr.Width - D(32) - D(30), rr.Height);
                // "more above / below" hint in the corner of the first / last visible row; the row text makes room for it
                string hint = slot == 0 && above > 0 ? "+" + above + " UP"
                            : slot == VisibleSteps - 1 && below > 0 ? "+" + below + " MORE" : "";
                if (hint != "")
                {
                    float hw = MicroW(g, hint, false);
                    Micro(g, hint, tr.Right - hw, slot == 0 ? rr.Y + D(3) : rr.Bottom - D(12), cMuted, false);
                    tr.Width -= (int)hw + D(8);
                }
                Color tc = l[i].Done ? cMuted : cInk;
                var tt = tr;
                if (anim && ms < 120f)
                {   // brief glitch: small jitter + RGB split, a quieter version of PaintComplete's
                    var rnd = new Random(frame * 7919 + i);
                    tt.Offset((int)Math.Round((rnd.NextDouble() * 2 - 1) * 1.5f * S), 0);
                    var tl = tt; tl.Offset(-D(2), 0);
                    var tg = tt; tg.Offset(D(2), 0);
                    TextIn(g, l[i].Text, fBody, Color.FromArgb(120, Hex("#FF2E88")), tl, StringAlignment.Near, StringAlignment.Center);
                    TextIn(g, l[i].Text, fBody, Color.FromArgb(120, Hex("#19E3FF")), tg, StringAlignment.Near, StringAlignment.Center);
                }
                TextIn(g, l[i].Text, fBody, tc, tt, StringAlignment.Near, StringAlignment.Center);
                if (l[i].Done)
                {   // strike-through; while animating it only reaches as far as the scanline
                    float tw = Math.Min(tr.Width, g.MeasureString(l[i].Text, fBody).Width - D(4));
                    float x0 = tr.X + D(2), x1 = Math.Min(tr.X + tw, sweepX);
                    if (x1 > x0)
                        using (var p = new Pen(cMuted, 1.2f * S)) g.DrawLine(p, x0, rr.Y + rr.Height / 2f + D(1), x1, rr.Y + rr.Height / 2f + D(1));
                }
                if (anim && ms < 280f) FillR(g, new Rectangle(Math.Max(rr.X, (int)sweepX - D(3)), rr.Y, D(3), rr.Height), accent);
                if (rowHov)
                {
                    Color xc = Hov("del" + i) ? Hex("#FF3B30") : cMuted;
                    float cx = del.X + del.Width / 2f, cy = del.Y + del.Height / 2f, u = 4f * S;
                    using (var p = new Pen(xc, 1.6f * S)) { g.DrawLine(p, cx - u, cy - u, cx + u, cy + u); g.DrawLine(p, cx - u, cy + u, cx + u, cy - u); }
                }
            }
            g.Restore(clip);

            if (scroll)
            {
                FillR(g, StepTrack, cHole);
                FillR(g, StepThumb, Hov("sbar") || sbDrag ? cInk : accent);
            }

            if (AddVisible) PaintField(g, fieldStep, txtStep.Focused);
        }

        void PaintVault(Graphics g)
        {
            var c = PanVault;
            Panel(g, c, "[03]", "VAULT");
            int n = VaultCount();
            string cnt = n + "/" + MaxVault;
            Micro(g, cnt, c.Right - D(14) - MicroW(g, cnt, true), c.Y + D(12), AccentText, true);

            bool taken = NextTaken;
            var clip = g.Save();
            g.SetClip(c, CombineMode.Intersect);   // nothing in here draws outside the panel
            for (int i = 0; i < MaxVault; i++)
            {
                var sr = R(Pad + 14, VaultY + VRowsTop + VRowH * i + 2, LW - 2 * Pad - 28, VRowH - 4);
                float p;
                int kind = VaultAnim(i, out p);
                Idea idea = vault[i], ghost = vGhost[i];

                if (idea != null)
                {
                    FillR(g, sr, cField);
                    LineR(g, sr, cLine, 1f);
                    if (kind == VStore)
                    {   // store: a scanline sweeps left to right and the row appears behind it; the age fades in last
                        float sp = Math.Min(1f, p / 0.8f);
                        sp = 1 - (1 - sp) * (1 - sp);
                        int sx = sr.X + (int)(sr.Width * sp);
                        var st = g.Save();
                        g.SetClip(new Rectangle(sr.X, sr.Y, sx - sr.X, sr.Height), CombineMode.Intersect);
                        PaintVaultRow(g, i, sr, idea, 255, (int)(255 * Math.Max(0f, (p - 0.7f) / 0.3f)), true, true, taken);
                        g.Restore(st);
                        if (sp < 1f) FillR(g, new Rectangle(Math.Max(sr.X, sx - D(2)), sr.Y, D(2), sr.Height), accent);
                    }
                    else PaintVaultRow(g, i, sr, idea, 255, 255, true, true, taken);
                }
                else if (kind == VMove && ghost != null)
                {   // upload: an accent tint sweeps right to left while the text fades, then the slot settles to empty
                    float sp = Math.Min(1f, p / 0.7f);
                    sp = 1 - (1 - sp) * (1 - sp);
                    float settle = p < 0.7f ? 0f : (p - 0.7f) / 0.3f;
                    int sx = sr.Right - (int)(sr.Width * sp);
                    if (settle > 0f) PaintVaultEmpty(g, sr, (int)(255 * settle));
                    int a = (int)(255 * (1 - settle));
                    FillR(g, sr, Fade(cField, a));
                    LineR(g, sr, Fade(cLine, a), 1f);
                    int ta = (int)(255 * (1 - Math.Min(1f, p / 0.6f)));
                    PaintVaultRow(g, i, sr, ghost, ta, ta, false, false, taken);
                    FillR(g, new Rectangle(sx, sr.Y, sr.Right - sx, sr.Height), Fade(accent, (int)(70 * (1 - settle))));
                    if (sp < 1f) FillR(g, new Rectangle(sx, sr.Y, D(2), sr.Height), accent);
                }
                else if (kind == VDelete && ghost != null)
                {   // lost signal: dim / normal / dim / normal with a small sideways jitter, then gone
                    bool dim = Math.Min(3, (int)(p * 4)) % 2 == 0;
                    var rnd = new Random(frame * 7919 + i);
                    var jr = sr;
                    jr.Offset((int)Math.Round((rnd.NextDouble() * 2 - 1) * 2f * S), 0);
                    int a = dim ? 60 : 255;
                    FillR(g, jr, Fade(cField, a));
                    LineR(g, jr, Fade(cLine, a), 1f);
                    PaintVaultRow(g, i, jr, ghost, a, a, true, false, taken);
                }
                else PaintVaultEmpty(g, sr, 255);
            }
            g.Restore(clip);

            if (n < MaxVault)
            {
                PaintField(g, fieldVault, txtVault.Focused);
                var add = R(LW - Pad - 14 - 64, VaultY + VAddY, 64, 30);
                Hit("vadd", add);
                PaintSolidBtn(g, add, "ADD", Hov("vadd"));
            }
            else Micro(g, "VAULT FULL // MOVE OR DELETE ONE", c.X + D(14), D(VaultY + VAddY + 10), cMuted, false);
        }

        void PaintVaultEmpty(Graphics g, Rectangle sr, int alpha)
        {
            using (var pen = new Pen(Fade(cLine, alpha), 1f) { DashStyle = DashStyle.Dash })
                g.DrawRectangle(pen, sr.X, sr.Y, sr.Width - 1, sr.Height - 1);
            Micro(g, "EMPTY SLOT", sr.X + D(8), sr.Y + D(6), Fade(cMuted, alpha), false);
        }

        // contents of a filled vault row: text, age, -> NEXT button and delete x.
        // alpha fades it (animations); buttons = draw them; live = they're also clickable.
        void PaintVaultRow(Graphics g, int i, Rectangle sr, Idea idea, int alpha, int ageAlpha, bool buttons, bool live, bool taken)
        {
            Color red = Hex("#FF3B30");
            bool sure = live && vConfirm == i;
            string mv = taken ? "NEXT TAKEN" : "-> NEXT";
            int delW = sure ? (int)MicroW(g, "SURE?", true) + D(10) : D(22);
            var del = new Rectangle(sr.Right - delW, sr.Y, delW, sr.Height);
            int mvW = (int)MicroW(g, mv, true) + D(12);
            var btn = new Rectangle(del.X - D(2) - mvW, sr.Y + D(3), mvW, sr.Height - D(6));

            // age nags harder the longer the idea sits: muted, accent from 14 days, red from 30
            int age = VaultAge(idea);
            string ages = age + "D";
            float ax = btn.X - D(8) - MicroW(g, ages, false);
            if (ageAlpha > 0) Micro(g, ages, ax, sr.Y + D(6), Fade(age >= 30 ? red : age >= 14 ? AccentText : cMuted, ageAlpha), false);

            var tr = new Rectangle(sr.X + D(8), sr.Y, (int)ax - D(8) - sr.X - D(8), sr.Height);
            if (alpha > 0)
                using (var b = new SolidBrush(Fade(cInk, alpha)))
                using (var sf = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                    g.DrawString(idea.Text, fBody, b, tr, sf);
            if (!buttons || alpha <= 0) return;

            string km = "vmove" + i, kd = "vdel" + i;
            if (live) { Hit(km, btn); Hit(kd, del); }
            bool hm = live && !taken && Hov(km);   // locked: no accent hover
            if (hm) FillR(g, btn, accent); else LineR(g, btn, Fade(taken ? cDim : cInk, alpha), 1.2f * S);
            Micro(g, mv, btn.X + D(6), btn.Y + D(3), hm ? OnColor(accent) : Fade(taken ? cMuted : cInk, alpha), true);

            if (sure) Micro(g, "SURE?", del.X + D(5), sr.Y + D(6), red, true);
            else
            {
                float cx = del.X + del.Width / 2f, cy = del.Y + del.Height / 2f, u = 3.5f * S;
                using (var pen = new Pen(Fade(live && Hov(kd) ? red : cMuted, alpha), 1.6f * S)) { g.DrawLine(pen, cx - u, cy - u, cx + u, cy + u); g.DrawLine(pen, cx - u, cy + u, cx + u, cy - u); }
            }
        }

        void PaintRoutinePanel(Graphics g, List<Routine> due, Day t)
        {
            var c = PanRoutine;
            Panel(g, c, "[R]", "ROUTINE");
            int done = 0;
            foreach (var r in due) if (RoutineDoneOn(r, todayKey)) done++;
            string cnt = done.ToString("00") + "/" + due.Count.ToString("00");
            Micro(g, cnt, c.Right - D(14) - MicroW(g, cnt, true), c.Y + D(12), AccentText, true);

            Color red = Hex("#FF3B30");
            for (int i = 0; i < due.Count; i++)
            {
                var r = due[i];
                string tag = RoutineTag(r), k = "rtn" + i;
                bool on = tag == "DONE", late = tag == "OVERDUE";
                var rr = R(Pad + 14, RtY + 30 + RtRowH * i, LW - 2 * Pad - 28, RtRowH);
                if (!t.Done) Hit(k, rr);
                if (!t.Done && Hov(k)) FillR(g, rr, cHole);

                var box = new Rectangle(rr.X + D(7), rr.Y + (rr.Height - D(14)) / 2, D(14), D(14));
                if (on)
                {
                    FillR(g, box, accent);
                    using (var p = new Pen(OnColor(accent), 1.8f * S) { StartCap = LineCap.Square, EndCap = LineCap.Square })
                        g.DrawLines(p, new[] { new PointF(box.X + 3f * S, box.Y + 7f * S), new PointF(box.X + 5.7f * S, box.Y + 10f * S), new PointF(box.X + 11f * S, box.Y + 4f * S) });
                }
                else using (var p = new Pen(late ? red : cInk, 1.5f * S)) g.DrawRectangle(p, box.X, box.Y, box.Width - 1, box.Height - 1);

                float tgw = MicroW(g, tag, true);
                Micro(g, tag, rr.Right - D(8) - tgw, rr.Y + D(8), late ? red : on ? cMuted : AccentText, true);

                var tr = new Rectangle(box.Right + D(10), rr.Y, (int)(rr.Right - D(18) - tgw - box.Right - D(10)), rr.Height);
                TextIn(g, r.Text, fBody, on ? cMuted : cInk, tr, StringAlignment.Near, StringAlignment.Center);
                if (on)
                {
                    float tw = Math.Min(tr.Width, g.MeasureString(r.Text, fBody).Width - D(4));
                    using (var p = new Pen(cMuted, 1.2f * S)) g.DrawLine(p, tr.X + D(2), rr.Y + rr.Height / 2f + D(1), tr.X + tw, rr.Y + rr.Height / 2f + D(1));
                }
            }
        }

        void PaintPunchButton(Graphics g)
        {
            var pb = R(Pad + 14, TY + PunchY, LW - 2 * Pad - 28, 60);
            Hit("punch", pb);
            int ls = StepsLeft(), lr = RoutinesLeft();
            bool locked = ls + lr > 0;
            float ua = locked ? 1f : UnlockAnim;   // < 0 waiting for the step stamp, 0..1 unlocking, 1 = idle
            if (locked || ua < 1f)
            {   // locked: every step and listed routine has to be checked first
                FillR(g, pb, cHole);
                LineR(g, pb, cDim, 1.2f * S);
                Hazard(g, new Rectangle(pb.Right - D(58), pb.Y, D(58), pb.Height), cDim, 7);
                WideCenterV(g, "LOCKED", fBtn, cMuted, pb.X + D(16), pb, WIDE + 0.1f);
                string l = "";
                if (ls > 0) l = ls.ToString("00") + (ls == 1 ? " STEP" : " STEPS");
                if (lr > 0) l += (l != "" ? " + " : "") + lr.ToString("00") + (lr == 1 ? " CHORE" : " CHORES");
                l += l == "" ? "00 LEFT" : " LEFT";
                Micro(g, l, pb.X + D(16), pb.Bottom - D(14), cMuted, true);
                if (locked || ua < 0f) return;

                // unlock: ink flash, then the ready button wipes in from the left while the hazard cap slides in from the right
                if (ua < 0.25f) { FillR(g, pb, cInk); return; }
                float wp = 1 - (float)Math.Pow(1 - (ua - 0.25f) / 0.75f, 3);
                int ww = (int)(pb.Width * wp), off = (int)(D(58) * (1 - wp));
                var st = g.Save();
                g.SetClip(new Rectangle(pb.X, pb.Y, ww, pb.Height));
                PaintPunchFace(g, pb, false, false);
                g.Restore(st);
                var cap = new Rectangle(pb.Right - D(58) + off, pb.Y, D(58) - off, pb.Height);
                if (cap.Width > 0)
                {
                    st = g.Save();
                    g.SetClip(cap);
                    FillR(g, cap, accent);
                    Hazard(g, new Rectangle(cap.X, cap.Y, D(58), cap.Height), Color.FromArgb(70, OnColor(accent)), 7);
                    g.Restore(st);
                }
                FillR(g, new Rectangle(Math.Min(pb.Right - D(3), pb.X + ww), pb.Y, D(3), pb.Height), cInk);
                return;
            }
            PaintPunchFace(g, pb, Hov("punch"), true);
        }

        // the ready-to-punch button; the unlock animation draws it without the hazard cap and adds that itself
        void PaintPunchFace(Graphics g, Rectangle pb, bool hv, bool hazard)
        {
            FillR(g, pb, hv ? cInk : accent);
            Color ink = hv ? OnColor(cInk) : OnColor(accent);
            // hazard end cap
            var hz = new Rectangle(pb.Right - D(58), pb.Y, D(58), pb.Height);
            if (hazard) Hazard(g, hz, Color.FromArgb(hv ? 60 : 70, ink), 7);
            WideCenterV(g, "PUNCH IT", fBtn, ink, pb.X + D(16), pb, WIDE + 0.1f);
            Chevrons(g, pb.Right - D(96), pb.Y + pb.Height / 2f, ink, 3);
            Micro(g, "CTRL+ENTER", pb.X + D(16), pb.Bottom - D(14), Color.FromArgb(150, ink), false);
        }

        void PaintComplete(Graphics g, Day t, int streak)
        {
            var box = R(Pad + 14, TY + PunchY, LW - 2 * Pad - 28, 58);
            float a = punchAnim;
#if PREVIEW
            if (Environment.GetEnvironmentVariable("OC_GLITCH") == "1") { a = 0.55f; frame = 5; }
#endif
            string time = t.DoneAt;
            DateTime dt;
            if (DateTime.TryParseExact(t.DoneAt, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
                time = dt.ToString("HH:mm", CultureInfo.InvariantCulture);

            if (a < 1f)
            {
                // glitch-in: slab grows, slices jitter, RGB split
                var rnd = new Random(frame * 7919);
                float grow = Math.Min(1f, a * 2.2f);
                grow = 1 - (float)Math.Pow(1 - grow, 3);
                float amp = (1 - a) * 18 * S;
                PaintGlitchSlab(g, box, grow, amp, rnd, accent);
                if (a > 0.35f) DrawCompleteText(g, box, time, OnColor(accent), (int)((rnd.NextDouble() * 2 - 1) * amp * 0.5f));
                using (var pen = new Pen(accent, 2f * S)) g.DrawRectangle(pen, PanToday);
            }
            else
            {
                FillR(g, box, accent);
                DrawCompleteText(g, box, time, OnColor(accent), 0);
            }

            // status line + undo
            float y = PanToday.Bottom - D(16);
            string msg = "LOGGED " + DateTime.Today.ToString("dd.MM", CultureInfo.InvariantCulture) + " // STREAK " + streak.ToString("00");
            if (streak == 3 || streak == 7 || streak == 14 || streak == 30 || streak == 50 || streak == 100)
                msg += " // MILESTONE";   // single-spaced so the longest line still clears UNDO
            Micro(g, msg, box.X, y, a >= 1 ? AccentText : cMuted, true);
            var undo = new Rectangle(box.Right - D(50), (int)y - D(5), D(50), D(18));
            Hit("undo", undo);
            float uw = MicroW(g, "UNDO", false);
            Micro(g, "UNDO", undo.Right - uw, y, Hov("undo") ? cInk : cMuted, false);
            if (Hov("undo")) using (var p = new Pen(cInk, 1)) g.DrawLine(p, undo.Right - uw, y + D(12), undo.Right, y + D(12));
        }

        // the glitch slab shared by the punch and the NEXT "QUEUED" animation: the box grown to `grow` of its width,
        // cut into slices that each jitter sideways by up to `amp` pixels, with a pink/cyan RGB split behind the fill
        void PaintGlitchSlab(Graphics g, Rectangle box, float grow, float amp, Random rnd, Color fill)
        {
            int w = (int)(box.Width * grow);
            int slices = 6, sh = box.Height / slices;
            for (int i = 0; i < slices; i++)
            {
                int off = (int)((rnd.NextDouble() * 2 - 1) * amp);
                var sr = new Rectangle(box.X + off, box.Y + i * sh, w, (i == slices - 1) ? box.Height - i * sh : sh);
                FillR(g, new Rectangle(sr.X - D(3), sr.Y, sr.Width, sr.Height), Color.FromArgb(160, Hex("#FF2E88")));
                FillR(g, new Rectangle(sr.X + D(3), sr.Y, sr.Width, sr.Height), Color.FromArgb(160, Hex("#19E3FF")));
                FillR(g, sr, fill);
            }
        }

        // NEXT field while tomorrow's task is being stamped in (the text box is hidden meanwhile)
        void PaintNextAnim(Graphics g)
        {
            var f = fieldTomorrow;
            float p = NextAnim;
            Color ink = OnColor(accent);
            var st = g.Save();
            g.SetClip(f, CombineMode.Intersect);   // nothing spills onto the QUEUE button or the panel
            if (p < NextAnimSplit)
            {   // glitch in: same moves as PaintComplete, with less jitter
                float a = p / NextAnimSplit;
                var rnd = new Random(frame * 7919);
                float grow = Math.Min(1f, a * 2.2f);
                grow = 1 - (float)Math.Pow(1 - grow, 3);
                float amp = (1 - a) * NextAnimJitter * S;
                FillR(g, f, cField);
                PaintGlitchSlab(g, f, grow, amp, rnd, accent);
                if (a > 0.35f)
                    WideCenterV(g, "QUEUED", fBtn, ink, f.X + D(14) + (int)((rnd.NextDouble() * 2 - 1) * amp * 0.5f), f, WIDE + 0.1f);
            }
            else
            {   // settle: the normal field with the saved task, the slab pulling away to the left over it
                float b = (p - NextAnimSplit) / (1f - NextAnimSplit);
                PaintField(g, f, false);
                // drawn where the text box will reappear, so the text doesn't jump
                using (var br = new SolidBrush(cInk))
                using (var sf = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.None })
                    g.DrawString(Get(Key(DateTime.Today.AddDays(1))).Task, fBody, br, txtTomorrow.Bounds, sf);
                float e = 1 - (float)Math.Pow(1 - b, 3);
                var slab = new Rectangle(f.X, f.Y, (int)(f.Width * (1 - e)), f.Height);
                if (slab.Width > 0)
                {
                    var ss = g.Save();
                    g.SetClip(slab, CombineMode.Intersect);
                    FillR(g, slab, accent);
                    WideCenterV(g, "QUEUED", fBtn, ink, f.X + D(14), f, WIDE + 0.1f);
                    g.Restore(ss);
                }
                LineR(g, f, Fade(accent, (int)(255 * (1 - b))), 2f * S);
            }
            g.Restore(st);
        }

        void DrawCompleteText(Graphics g, Rectangle box, string time, Color ink, int jitter)
        {
            // check box glyph
            var cb = new Rectangle(box.X + D(16) + jitter, box.Y + (box.Height - D(22)) / 2, D(22), D(22));
            using (var p = new Pen(ink, 2f * S)) g.DrawRectangle(p, cb);
            using (var p = new Pen(ink, 2.6f * S) { StartCap = LineCap.Square, EndCap = LineCap.Square })
                g.DrawLines(p, new[] { new PointF(cb.X + 5 * S, cb.Y + 11 * S), new PointF(cb.X + 9.5f * S, cb.Y + 16 * S), new PointF(cb.X + 17 * S, cb.Y + 6 * S) });
            WideCenterV(g, "COMPLETE", fBtn, ink, cb.Right + D(12), box, WIDE + 0.1f);
            float tw = WideW(g, time, fBtn, WIDE);
            WideCenterV(g, time, fBtn, ink, box.Right - D(16) - tw + jitter, box, WIDE);
        }

        void PaintField(Graphics g, Rectangle f, bool focused)
        {
            FillR(g, f, cField);
            LineR(g, f, focused ? accent : cLine, focused ? 2f * S : 1f);
            if (focused) FillR(g, new Rectangle(f.X, f.Y, D(4), f.Height), accent);
        }

        void PaintSolidBtn(Graphics g, Rectangle r, string s, bool hv)
        {
            FillR(g, r, hv ? accent : cInk);
            Color ink = hv ? OnColor(accent) : OnColor(cInk);
            float w = WideW(g, s, fMid, 1.04f);
            WideCenterV(g, s, fMid, ink, r.X + (r.Width - w) / 2f, r, 1.04f);
        }

        void PaintOutlineBtn(Graphics g, Rectangle r, string s, bool hv)
        {
            if (hv) FillR(g, r, accent);
            else LineR(g, r, cInk, 1.5f * S);
            Color ink = hv ? OnColor(accent) : cInk;
            float w = WideW(g, s, fMid, 1.04f);
            WideCenterV(g, s, fMid, ink, r.X + (r.Width - w) / 2f, r, 1.04f);
        }

        void PaintSettings(Graphics g)
        {
            Wide(g, "CONFIG", fHuge, cInk, D(Pad - 2), D(46), WIDE);
            Micro(g, "PERSONALIZE THIS TERMINAL", D(Pad), D(96), cMuted, false);
            if (flash != "") { float fw = MicroW(g, flash, true); Micro(g, flash, D(LW - Pad) - fw, D(96), AccentText, true); }
            var hz = R(Pad, 118, LW - 2 * Pad, 8);
            FillR(g, hz, cHole); Hazard(g, hz, cDim, 5);
            FillR(g, new Rectangle(hz.X, hz.Y, D(60), hz.Height), accent);

            // accent
            var c = R(Pad, 138, LW - 2 * Pad, 134);
            Panel(g, c, "[A]", "ACCENT");
            string an = (rotate ? "ROTATING // " : "") + accentName.ToUpperInvariant();
            Micro(g, an, c.Right - D(14) - MicroW(g, an, true), c.Y + D(12), AccentText, true);
            int nSw = Accents.Length + 1;
            float sw = D(32), gap = (c.Width - D(28) - nSw * sw) / (nSw - 1f);
            for (int i = 0; i < Accents.Length + 1; i++)
            {
                var r = new Rectangle((int)(c.X + D(14) + i * (sw + gap)), c.Y + D(36), (int)sw, (int)sw);
                bool sel;
                if (i < Accents.Length)
                {
                    Hit("acc" + i, r);
                    FillR(g, r, Accents[i].Color);
                    sel = Accents[i].Color.ToArgb() == accent.ToArgb() && accentName != "Custom";
                }
                else
                {
                    Hit("accCustom", r);
                    using (var pen = new Pen(Hov("accCustom") ? cInk : cMuted, 1.2f * S) { DashStyle = DashStyle.Dash })
                        g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                    TextIn(g, "+", fMid, Hov("accCustom") ? cInk : cMuted, r, StringAlignment.Center, StringAlignment.Center);
                    sel = accentName == "Custom";
                }
                if (sel) using (var pen = new Pen(cInk, 2f * S)) g.DrawRectangle(pen, Rectangle.Inflate(r, D(4), D(4)));
                else if (Hov(i < Accents.Length ? "acc" + i : "accCustom"))
                    using (var pen = new Pen(cDim, 1.5f * S)) g.DrawRectangle(pen, Rectangle.Inflate(r, D(3), D(3)));
            }
            PaintToggle(g, "togRotate", "Daily rotation", rotate, c.Y + D(82), c);

            // theme
            c = R(Pad, 282, LW - 2 * Pad, 80);
            Panel(g, c, "[B]", "THEME");
            float half = (c.Width - D(28) - D(6)) / 2f;
            var rd = new Rectangle(c.X + D(14), c.Y + D(32), (int)half, D(36));
            var rl = new Rectangle(rd.Right + D(6), rd.Y, (int)half, D(36));
            Hit("themeDark", rd); Hit("themeLight", rl);
            PaintSeg(g, rd, "DARK", dark, Hov("themeDark"));
            PaintSeg(g, rl, "LIGHT", !dark, Hov("themeLight"));

            // toggles
            c = R(Pad, 372, LW - 2 * Pad, 252);
            Panel(g, c, "[C]", "SYSTEM");
            PaintSoundRow(g, true, "Punch sound", sound, punchName, c.Y + D(28), c);
            PaintSoundRow(g, false, "Startup sound", bootSound, bootName, c.Y + D(72), c);
            PaintToggle(g, "togUi", "Interface blips", uiSound, c.Y + D(116), c);
            PaintToggle(g, "togTop", "Keep on top of other windows", onTop, c.Y + D(160), c);
            PaintToggle(g, "togStart", "Open when Windows starts", StartsWithWindows(), c.Y + D(204), c);

            // routines: opens its own screen
            var rb = R(Pad, 634, LW - 2 * Pad, 40);
            Hit("routines", rb);
            bool rh = Hov("routines");
            if (rh) FillR(g, rb, accent); else LineR(g, rb, cInk, 1.5f * S);
            Color ri = rh ? OnColor(accent) : cInk;
            float rw = WideW(g, "ROUTINES", fMid, 1.04f);
            WideCenterV(g, "ROUTINES", fMid, ri, rb.X + D(16), rb, 1.04f);
            Chevrons(g, rb.X + D(16) + rw + D(10), rb.Y + rb.Height / 2f, ri, 1);
            string rc = routines.Count.ToString("00") + " SET";
            Micro(g, rc, rb.Right - D(16) - MicroW(g, rc, true), rb.Y + D(16), rh ? ri : AccentText, true);

            var link = R(Pad, 682, 200, 20);
            Hit("openData", link);
            Micro(g, "OPEN DATA FOLDER  >", link.X, link.Y + D(5), Hov("openData") ? AccentText : cMuted, true);

            PaintBigBtn(g, "back", R(Pad, 708, LW - 2 * Pad, 52), "RETURN");
        }

        // full-width accent button with a hazard end cap (RETURN / BACK)
        void PaintBigBtn(Graphics g, string key, Rectangle r, string s)
        {
            Hit(key, r);
            bool hv = Hov(key);
            FillR(g, r, hv ? cInk : accent);
            Color ink = hv ? OnColor(cInk) : OnColor(accent);
            Hazard(g, new Rectangle(r.Right - D(58), r.Y, D(58), r.Height), Color.FromArgb(70, ink), 7);
            WideCenterV(g, s, fBtn, ink, r.X + D(16), r, WIDE + 0.1f);
        }

        void PaintRoutines(Graphics g)
        {
            Wide(g, "ROUTINES", fHuge, cInk, D(Pad - 2), D(46), WIDE);
            Micro(g, "SET ONCE  //  REPEATS EVERY WEEK", D(Pad), D(96), cMuted, false);
            if (flash != "") { float fw = MicroW(g, flash, true); Micro(g, flash, D(LW - Pad) - fw, D(96), AccentText, true); }
            var hz = R(Pad, 118, LW - 2 * Pad, 8);
            FillR(g, hz, cHole); Hazard(g, hz, cDim, 5);
            FillR(g, new Rectangle(hz.X, hz.Y, D(60), hz.Height), accent);

            var c = R(Pad, 138, LW - 2 * Pad, RPanelH);
            Panel(g, c, "[D]", "WEEKLY");
            string cnt = routines.Count.ToString("00") + "/" + MaxRoutines.ToString("00");
            Micro(g, cnt, c.Right - D(14) - MicroW(g, cnt, true), c.Y + D(12), AccentText, true);

            if (routines.Count == 0)
                Micro(g, "NOTHING REPEATING YET. ADD ONE BELOW.", c.X + D(14), D(RRowTop + 8), cMuted, false);

            string[] dl = { "S", "M", "T", "W", "T", "F", "S" };
            int todayDow = (int)TodayDate.DayOfWeek;
            Color red = Hex("#FF3B30");
            for (int i = 0; i < routines.Count; i++)
            {
                var r = routines[i];
                int y = RRowTop + RRowH * i;
                if (i > 0) using (var p = new Pen(cLine, 1)) g.DrawLine(p, c.X + D(14), D(y), c.Right - D(14), D(y));

                // text + delete
                TextIn(g, r.Text, fBody, cInk, R(Pad + 14, y + 4, LW - 2 * Pad - 28 - 30, 22), StringAlignment.Near, StringAlignment.Center);
                var del = R(LW - Pad - 14 - 24, y + 4, 24, 22);
                Hit("rdel" + i, del);
                float cx = del.X + del.Width / 2f, cy = del.Y + del.Height / 2f, u = 4f * S;
                using (var p = new Pen(Hov("rdel" + i) ? red : cMuted, 1.6f * S)) { g.DrawLine(p, cx - u, cy - u, cx + u, cy + u); g.DrawLine(p, cx - u, cy + u, cx + u, cy - u); }

                // day chips, Sunday first; today's gets a small underline
                for (int d = 0; d < 7; d++)
                {
                    var ch = R(Pad + 14 + d * 30, y + 27, 26, 18);
                    string k = "rday" + i + "_" + d;
                    Hit(k, ch);
                    bool on = r.Days[d], hv = Hov(k);
                    if (on) FillR(g, ch, hv ? cInk : accent);
                    else { FillR(g, ch, hv ? cHole : cField); LineR(g, ch, hv ? cInk : cLine, 1f); }
                    Color ink = on ? OnColor(hv ? cInk : accent) : (hv ? cInk : cMuted);
                    float mw = MicroW(g, dl[d], true) - 1.1f * S;
                    Micro(g, dl[d], ch.X + (ch.Width - mw) / 2f, ch.Y + D(4), ink, true);
                    if (d == todayDow) FillR(g, new Rectangle(ch.X, ch.Bottom + D(2), ch.Width, D(2)), cInk);
                }

                // current state, same tags as the main screen
                string tag = RoutineTag(r);
                if (tag != "")
                {
                    float tw = MicroW(g, tag, true);
                    Micro(g, tag, c.Right - D(14) - tw, D(y + 31), tag == "OVERDUE" ? red : tag == "DONE" ? cMuted : AccentText, true);
                }
            }

            if (routines.Count < MaxRoutines)
            {
                PaintField(g, fieldRoutine, txtRoutine.Focused);
                var add = R(LW - Pad - 14 - 80, RFieldY, 80, 36);
                Hit("addRoutine", add);
                PaintSolidBtn(g, add, "ADD", Hov("addRoutine"));
            }
            else Micro(g, "MAX 08. DELETE ONE TO ADD ANOTHER.", c.X + D(14), D(RFieldY + 4), cMuted, false);

            PaintBigBtn(g, "rback", R(Pad, RBackY, LW - 2 * Pad, 52), "BACK");
        }

        void PaintSeg(Graphics g, Rectangle r, string s, bool on, bool hv)
        {
            if (on) FillR(g, r, accent);
            else { FillR(g, r, hv ? cHole : cField); LineR(g, r, cLine, 1f); }
            Color ink = on ? OnColor(accent) : cInk;
            float w = WideW(g, s, fMid, 1.04f);
            WideCenterV(g, s, fMid, ink, r.X + (r.Width - w) / 2f, r, 1.04f);
        }

        void PaintSoundRow(Graphics g, bool punch, string label, bool on, string file, int y, Rectangle card)
        {
            string kTog = punch ? "togSound" : "togBoot", kChg = punch ? "chgPunch" : "chgBoot", kRst = punch ? "rstPunch" : "rstBoot";
            var sw = new Rectangle(card.Right - D(14) - D(52), y + D(11), D(52), D(22));
            var chg = new Rectangle(sw.X - D(10) - D(64), sw.Y, D(64), D(22));
            Hit(kChg, chg);
            Hit(kTog, new Rectangle(sw.X - D(4), y, card.Right - sw.X + D(4), D(44)));
            TextIn(g, label, fBody, cInk, new Rectangle(card.X + D(14), y + D(4), chg.X - card.X - D(20), D(22)), StringAlignment.Near, StringAlignment.Center);
            string src = file == "" ? "BUILT-IN" : "FILE: " + file.ToUpperInvariant();
            if (src.Length > 19) src = src.Substring(0, 18) + "~";   // leaves room for RESET before the CHANGE button
            float w = Micro(g, src, card.X + D(14), y + D(28), cMuted, false);
            if (file != "")
            {
                var rst = new Rectangle((int)(card.X + D(14) + w + D(6)), y + D(24), D(44), D(16));
                Hit(kRst, rst);
                Micro(g, "RESET", rst.X + D(2), y + D(28), Hov(kRst) ? cInk : AccentText, true);
            }
            if (Hov(kChg)) FillR(g, chg, accent); else LineR(g, chg, cDim, 1.2f * S);
            float mw = MicroW(g, "CHANGE", true);
            Micro(g, "CHANGE", chg.X + (chg.Width - mw) / 2f, chg.Y + D(6), Hov(kChg) ? OnColor(accent) : cInk, true);
            PaintSwitch(g, sw, on);
        }

        void PaintSwitch(Graphics g, Rectangle sw, bool on)
        {
            if (on) FillR(g, sw, accent); else LineR(g, sw, cDim, 1.5f * S);
            var knob = on ? new Rectangle(sw.Right - D(20), sw.Y + D(4), D(16), sw.Height - D(8))
                          : new Rectangle(sw.X + D(4), sw.Y + D(4), D(16), sw.Height - D(8));
            FillR(g, knob, on ? OnColor(accent) : cMuted);
            string st = on ? "ON" : "OFF";
            float mw = MicroW(g, st, true);
            float tx = on ? sw.X + D(6) : sw.Right - D(6) - mw;
            Micro(g, st, tx, sw.Y + D(6), on ? OnColor(accent) : cMuted, true);
        }

        void PaintToggle(Graphics g, string key, string label, bool on, int y, Rectangle card)
        {
            var row = new Rectangle(card.X, y, card.Width, D(44));
            Hit(key, row);
            if (Hov(key)) FillR(g, new Rectangle(card.X + D(1), y, card.Width - D(2), D(44)), cHole);
            TextIn(g, label, fBody, cInk, new Rectangle(card.X + D(14), y, card.Width - D(100), D(44)), StringAlignment.Near, StringAlignment.Center);
            // square switch with ON/OFF readout
            var sw = new Rectangle(card.Right - D(14) - D(52), y + D(11), D(52), D(22));
            if (on) FillR(g, sw, accent); else LineR(g, sw, cDim, 1.5f * S);
            var knob = on ? new Rectangle(sw.Right - D(20), sw.Y + D(4), D(16), sw.Height - D(8))
                          : new Rectangle(sw.X + D(4), sw.Y + D(4), D(16), sw.Height - D(8));
            FillR(g, knob, on ? OnColor(accent) : cMuted);
            string st = on ? "ON" : "OFF";
            float mw = MicroW(g, st, true);
            float tx = on ? sw.X + D(6) : sw.Right - D(6) - mw;
            Micro(g, st, tx, sw.Y + D(6), on ? OnColor(accent) : cMuted, true);
        }

        // ---------- window + input ----------
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, int w, int l);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x20000;       // WS_MINIMIZEBOX: taskbar click minimizes/restores
                cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int square = 1; DwmSetWindowAttribute(Handle, 33, ref square, 4); } catch { } // Win11: keep corners square
        }

        string HitAt(Point p)
        {
            foreach (var kv in hits) if (kv.Value.Contains(p)) return kv.Key;
            return "";
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (sbDrag)
            {
                Rectangle tk = StepTrack, th = StepThumb;
                int span = tk.Height - th.Height, max = Steps(todayKey).Count - VisibleSteps;
                if (span > 0 && max > 0) ScrollTo((int)Math.Round((e.Y - sbDragOff - tk.Y) * (double)max / span));
                return;
            }
            string h = HitAt(e.Location);
            if (h != hover)
            {
                hover = h;
                Cursor = h == "" ? Cursors.Default : Cursors.Hand;
                if (h != "") Sound(Sfx.Tick);
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover != "") { hover = ""; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            string h = HitAt(e.Location);
            if (h == "" && e.Y < D(36))
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, 0x2, 0); // drag the window
                return;
            }
            if (h == "") { ActiveControl = null; Invalidate(); return; }
            if (h == "sbar")
            {   // on the thumb: drag it; above or below it: page
                var th = StepThumb;
                if (e.Y < th.Y) ScrollTo(stepScroll - VisibleSteps);
                else if (e.Y >= th.Bottom) ScrollTo(stepScroll + VisibleSteps);
                else { sbDrag = true; sbDragOff = e.Y - th.Y; Invalidate(); }
                return;
            }
            DoClick(h);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (sbDrag) { sbDrag = false; Invalidate(); }
        }

        void DoClick(string h)
        {
            // these play their own sound (save / undo / click depending on the result)
            if (h != "punch" && h != "undo" && h != "setToday" && h != "saveTomorrow" && h != "addRoutine"
                && h != "vadd" && !h.StartsWith("vmove") && !h.StartsWith("vdel")
                && !h.StartsWith("step") && !h.StartsWith("del") && !h.StartsWith("rtn") && !h.StartsWith("rdel")) Sound(Sfx.Click);
            switch (h)
            {
                case "close": Close(); break;
                case "min": WindowState = FormWindowState.Minimized; break;
                case "gear": showSettings = !showSettings; showRoutines = false; editing = false; Relayout(); break;
                case "back": showSettings = false; showRoutines = false; Relayout(); break;
                case "routines": showRoutines = true; ActiveControl = null; Relayout(); break;
                case "rback": showRoutines = false; ActiveControl = null; Relayout(); break;
                case "addRoutine": AddRoutine(); break;
                case "setToday": SetToday(); break;
                case "editTask": editing = true; Relayout(); break;
                case "punch": Punch(); break;
                case "undo": Unpunch(); break;
                case "saveTomorrow": SaveTomorrow(); break;
                case "vadd": AddVault(); break;
                case "themeDark": dark = true; ApplyTheme(); SaveSettings(); Invalidate(); break;
                case "themeLight": dark = false; ApplyTheme(); SaveSettings(); Invalidate(); break;
                case "togSound": sound = !sound; if (sound) Sfx.Play(customPunch ?? Sfx.Punch, true); SaveSettings(); Invalidate(); break;
                case "togBoot": bootSound = !bootSound; if (bootSound) Sfx.Play(customBoot ?? Sfx.Boot, true); SaveSettings(); Invalidate(); break;
                case "chgPunch": PickSound(true); break;
                case "chgBoot": PickSound(false); break;
                case "rstPunch": ResetSound(true); break;
                case "rstBoot": ResetSound(false); break;
                case "togUi": uiSound = !uiSound; SaveSettings(); Invalidate(); break;
                case "togTop": onTop = !onTop; TopMost = onTop; SaveSettings(); Invalidate(); break;
                case "togStart": SetStartWithWindows(!StartsWithWindows()); Invalidate(); break;
                case "togRotate": rotate = !rotate; ApplyRotation(); SaveSettings(); Invalidate(); break;
                case "openData": try { Process.Start("explorer.exe", "\"" + dataDir + "\""); } catch { } break;
                case "accCustom":
                    using (var cd = new ColorDialog { FullOpen = true, Color = accent })
                        if (cd.ShowDialog(this) == DialogResult.OK) { accent = cd.Color; accentName = "Custom"; rotate = false; SaveSettings(); Invalidate(); }
                    break;
                default:
                    int si;
                    if (h.StartsWith("rtn") && int.TryParse(h.Substring(3), out si)) { ToggleRoutine(si); break; }
                    if (h.StartsWith("rdel") && int.TryParse(h.Substring(4), out si)) { DeleteRoutine(si); break; }
                    if (h.StartsWith("vmove") && int.TryParse(h.Substring(5), out si)) { MoveVault(si); break; }
                    if (h.StartsWith("vdel") && int.TryParse(h.Substring(4), out si)) { DeleteVault(si); break; }
                    if (h.StartsWith("rday"))
                    {   // rday{routine}_{weekday}
                        var p = h.Substring(4).Split('_');
                        int ri, di;
                        if (p.Length == 2 && int.TryParse(p[0], out ri) && int.TryParse(p[1], out di)) ToggleRoutineDay(ri, di);
                        break;
                    }
                    if (h.StartsWith("step") && int.TryParse(h.Substring(4), out si)) { ToggleStep(si); break; }
                    if (h.StartsWith("del") && int.TryParse(h.Substring(3), out si)) { DeleteStep(si); break; }
                    if (h.StartsWith("acc"))
                    {
                        int i;
                        if (int.TryParse(h.Substring(3), out i) && i < Accents.Length)
                        { accent = Accents[i].Color; accentName = Accents[i].Name; rotate = false; SaveSettings(); Invalidate(); }
                    }
                    break;
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Control && e.KeyCode == Keys.Enter && !showSettings) { e.SuppressKeyPress = true; Punch(); }
            else if (e.KeyCode == Keys.Escape && showSettings)
            {   // back one level: routines -> config -> main
                e.SuppressKeyPress = true;
                if (showRoutines) showRoutines = false; else showSettings = false;
                ActiveControl = null;
                Relayout();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (WindowState == FormWindowState.Normal) SaveSettings();
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Application.RemoveMessageFilter(this);
            base.OnFormClosed(e);
        }
    }
}
