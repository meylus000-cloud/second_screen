using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace TabMonitor
{
    static class Native
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public POINT pt; }

        [StructLayout(LayoutKind.Sequential)]
        public struct ICONINFO { public bool fIcon; public int xHotspot; public int yHotspot; public IntPtr hbmMask; public IntPtr hbmColor; }

        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO pci);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO info);
        [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);

        public const uint LEFTDOWN = 0x0002, LEFTUP = 0x0004, RIGHTDOWN = 0x0008, RIGHTUP = 0x0010;
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    sealed class MainForm : Form
    {
        const int Port = 7777;

        ComboBox cbScreen;
        TextBox tbIp;
        CheckBox chTouch;
        TrackBar tbQuality;
        Label lbQuality;
        NumericUpDown nFps;
        Button btn;
        Label status;
        TextBox log;

        Thread worker;
        volatile bool run;
        volatile int selScreen;
        volatile int quality = 60;
        volatile int fps = 30;
        volatile bool touchEnabled = true;
        volatile string ipText = "";

        string settingsFile;

        public MainForm()
        {
            Text = "Другий екран — ноутбук";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(470, 440);
            Font = new Font("Segoe UI", 9f);

            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabMonitor");
            settingsFile = Path.Combine(dir, "settings.txt");

            int y = 14;
            AddLabel("Який екран показувати на планшеті:", 14, y);
            cbScreen = new ComboBox { Left = 14, Top = y + 20, Width = 440, DropDownStyle = ComboBoxStyle.DropDownList };
            Controls.Add(cbScreen);
            FillScreens();
            cbScreen.SelectedIndexChanged += delegate { selScreen = Math.Max(0, cbScreen.SelectedIndex); };

            y += 58;
            AddLabel("IP планшета (лише для Wi-Fi; для кабелю залиште порожнім):", 14, y);
            tbIp = new TextBox { Left = 14, Top = y + 20, Width = 440 };
            Controls.Add(tbIp);
            tbIp.TextChanged += delegate { ipText = tbIp.Text; };

            y += 58;
            lbQuality = AddLabel("Якість картинки: 60", 14, y);
            tbQuality = new TrackBar { Left = 8, Top = y + 18, Width = 300, Minimum = 20, Maximum = 95, Value = 60, TickFrequency = 10 };
            Controls.Add(tbQuality);
            tbQuality.ValueChanged += delegate { quality = tbQuality.Value; lbQuality.Text = "Якість картинки: " + tbQuality.Value; };

            AddLabel("Кадрів/с:", 330, y);
            nFps = new NumericUpDown { Left = 330, Top = y + 22, Width = 70, Minimum = 5, Maximum = 60, Value = 30 };
            Controls.Add(nFps);
            nFps.ValueChanged += delegate { fps = (int)nFps.Value; };

            y += 66;
            chTouch = new CheckBox { Left = 14, Top = y, Width = 440, Text = "Дотик до планшета керує мишею (2 пальці = права кнопка)", Checked = true };
            Controls.Add(chTouch);
            chTouch.CheckedChanged += delegate { touchEnabled = chTouch.Checked; };

            y += 34;
            btn = new Button { Left = 14, Top = y, Width = 440, Height = 40, Text = "Запустити" };
            btn.Click += delegate { Toggle(); };
            Controls.Add(btn);

            y += 50;
            status = new Label { Left = 14, Top = y, Width = 440, Height = 20, Text = "Зупинено", Font = new Font(Font, FontStyle.Bold) };
            Controls.Add(status);

            y += 24;
            log = new TextBox { Left = 14, Top = y, Width = 440, Height = 110, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
            Controls.Add(log);

            LoadSettings();
            FormClosing += delegate { SaveSettings(); Stop(); };
        }

        Label AddLabel(string t, int x, int y)
        {
            Label l = new Label { Left = x, Top = y, Width = 440, Height = 18, Text = t };
            Controls.Add(l);
            return l;
        }

        void FillScreens()
        {
            cbScreen.Items.Clear();
            Screen[] all = Screen.AllScreens;
            for (int i = 0; i < all.Length; i++)
            {
                Screen s = all[i];
                cbScreen.Items.Add("Екран " + (i + 1) + " — " + s.Bounds.Width + "x" + s.Bounds.Height + (s.Primary ? " (основний)" : ""));
            }
            cbScreen.SelectedIndex = 0;
        }

        // ---------- налаштування ----------

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(settingsFile)) return;
                foreach (string line in File.ReadAllLines(settingsFile))
                {
                    int p = line.IndexOf('=');
                    if (p < 0) continue;
                    string k = line.Substring(0, p), v = line.Substring(p + 1);
                    if (k == "ip") tbIp.Text = v;
                    else if (k == "quality") { int q; if (int.TryParse(v, out q) && q >= tbQuality.Minimum && q <= tbQuality.Maximum) tbQuality.Value = q; }
                    else if (k == "fps") { int f; if (int.TryParse(v, out f) && f >= nFps.Minimum && f <= nFps.Maximum) nFps.Value = f; }
                    else if (k == "touch") chTouch.Checked = v == "1";
                    else if (k == "screen") { int s; if (int.TryParse(v, out s) && s >= 0 && s < cbScreen.Items.Count) cbScreen.SelectedIndex = s; }
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsFile));
                File.WriteAllLines(settingsFile, new string[] {
                    "ip=" + tbIp.Text, "quality=" + tbQuality.Value, "fps=" + nFps.Value,
                    "touch=" + (chTouch.Checked ? "1" : "0"), "screen=" + cbScreen.SelectedIndex });
            }
            catch { }
        }

        // ---------- керування ----------

        void Toggle()
        {
            if (run) Stop(); else Start();
        }

        void Start()
        {
            if (worker != null && worker.IsAlive) worker.Join(3000);
            run = true;
            btn.Text = "Зупинити";
            worker = new Thread(Work) { IsBackground = true };
            worker.Start();
        }

        void Stop()
        {
            run = false;
            btn.Text = "Запустити";
            SetStatus("Зупинено");
        }

        void SetStatus(string s)
        {
            try { BeginInvoke(new Action(delegate { status.Text = s; })); } catch { }
        }

        void Log(string s)
        {
            try
            {
                BeginInvoke(new Action(delegate
                {
                    log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + "\r\n");
                }));
            }
            catch { }
        }

        // ---------- робочий потік ----------

        void Work()
        {
            Log("Запущено. Підключіть планшет (кабель або Wi-Fi) і відкрийте на ньому «Другий екран».");
            while (run)
            {
                TcpClient c; int tw, th;
                SetStatus("Шукаю планшет...");
                if (!Connect(out c, out tw, out th))
                {
                    for (int i = 0; i < 15 && run; i++) Thread.Sleep(100);
                    continue;
                }
                SetStatus("Підключено (планшет " + tw + "x" + th + ")");
                try { StreamLoop(c, tw, th); }
                catch (Exception ex) { Log("Помилка: " + ex.Message); }
                finally { try { c.Close(); } catch { } }
                if (run) { SetStatus("З'єднання втрачено, пробую знову..."); Log("З'єднання втрачено."); }
            }
        }

        bool Connect(out TcpClient client, out int tw, out int th)
        {
            client = null; tw = 0; th = 0;
            List<string> hosts = new List<string>();
            string ip = (ipText ?? "").Trim();

            if (ip.Length > 0)
            {
                hosts.Add(ip);
            }
            else
            {
                if (TryAdbForward()) hosts.Add("127.0.0.1");
                try
                {
                    foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        foreach (GatewayIPAddressInformation g in ni.GetIPProperties().GatewayAddresses)
                        {
                            if (g.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                            string s = g.Address.ToString();
                            if (s != "0.0.0.0" && !hosts.Contains(s)) hosts.Add(s);
                        }
                    }
                }
                catch { }
                if (!hosts.Contains("192.168.42.129")) hosts.Add("192.168.42.129");
            }

            foreach (string h in hosts)
            {
                if (!run) return false;
                if (TryHost(h, out client, out tw, out th))
                {
                    Log("Підключено до " + h);
                    return true;
                }
            }
            return false;
        }

        bool adbWarned;

        bool TryAdbForward()
        {
            try
            {
                string adb = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb.exe");
                if (!File.Exists(adb)) return false;
                ProcessStartInfo psi = new ProcessStartInfo(adb, "forward tcp:" + Port + " tcp:" + Port);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return false; }
                    if (p.ExitCode == 0)
                    {
                        if (!adbWarned) { adbWarned = true; Log("Знайдено планшет через USB-налагодження (adb)."); }
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        bool TryHost(string host, out TcpClient client, out int tw, out int th)
        {
            client = null; tw = 0; th = 0;
            TcpClient c = new TcpClient();
            try
            {
                IAsyncResult ar = c.BeginConnect(host, Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(900)) { c.Close(); return false; }
                c.EndConnect(ar);
                c.NoDelay = true;
                c.ReceiveTimeout = 2500;
                byte[] hs = ReadExact(c.GetStream(), 8);
                if (hs == null || hs[0] != (byte)'T' || hs[1] != (byte)'B' || hs[2] != (byte)'M' || hs[3] != (byte)'1')
                {
                    c.Close(); return false;
                }
                tw = (hs[4] << 8) | hs[5];
                th = (hs[6] << 8) | hs[7];
                if (tw < 64 || th < 64) { c.Close(); return false; }
                c.ReceiveTimeout = 0;
                client = c;
                return true;
            }
            catch
            {
                try { c.Close(); } catch { }
                return false;
            }
        }

        static byte[] ReadExact(Stream s, int n)
        {
            byte[] b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = s.Read(b, got, n - got);
                if (r <= 0) return null;
                got += r;
            }
            return b;
        }

        static Rectangle GetBounds(int idx)
        {
            Screen[] all = Screen.AllScreens;
            if (idx < 0 || idx >= all.Length) idx = 0;
            return all[idx].Bounds;
        }

        // ---------- передача кадрів ----------

        sealed class Session
        {
            public volatile bool Alive = true;
            public bool LeftDown;
        }

        void StreamLoop(TcpClient c, int tw, int th)
        {
            NetworkStream ns = c.GetStream();
            Session sess = new Session();
            SemaphoreSlim credit = new SemaphoreSlim(2, 2);

            Thread reader = new Thread(delegate () { ReadEvents(ns, sess, credit); });
            reader.IsBackground = true;
            reader.Start();

            ImageCodecInfo jpg = GetJpegCodec();
            MemoryStream ms = new MemoryStream(512 * 1024);
            byte[] prev = new byte[0];
            int prevLen = -1;
            Bitmap src = null, dst = null;
            int dx = 0, dy = 0, dw = 0, dh = 0;
            Stopwatch sw = new Stopwatch();
            Stopwatch lastSend = Stopwatch.StartNew();
            byte[] keepAlive = new byte[4];

            try
            {
                while (run && sess.Alive)
                {
                    sw.Restart();
                    Rectangle b = GetBounds(selScreen);

                    if (src == null || src.Width != b.Width || src.Height != b.Height)
                    {
                        if (src != null) src.Dispose();
                        if (dst != null) dst.Dispose();
                        src = new Bitmap(b.Width, b.Height, PixelFormat.Format32bppRgb);

                        double scale = Math.Min((double)tw / b.Width, (double)th / b.Height);
                        dw = Math.Max(2, ((int)(b.Width * scale)) & ~1);
                        dh = Math.Max(2, ((int)(b.Height * scale)) & ~1);
                        dst = new Bitmap(dw, dh, PixelFormat.Format32bppRgb);
                        dx = 0; dy = 0;
                        prevLen = -1;
                    }

                    using (Graphics g = Graphics.FromImage(src))
                    {
                        g.CopyFromScreen(b.Location, Point.Empty, b.Size, CopyPixelOperation.SourceCopy);
                        DrawCursor(g, b);
                    }

                    using (Graphics g = Graphics.FromImage(dst))
                    {
                        g.CompositingQuality = CompositingQuality.HighSpeed;
                        g.SmoothingMode = SmoothingMode.HighSpeed;
                        g.InterpolationMode = InterpolationMode.Bilinear;
                        g.DrawImage(src, new Rectangle(dx, dy, dw, dh), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
                    }

                    ms.SetLength(0);
                    ms.Write(new byte[4], 0, 4);
                    using (EncoderParameters ep = new EncoderParameters(1))
                    {
                        ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                        dst.Save(ms, jpg, ep);
                    }

                    byte[] buf = ms.GetBuffer();
                    int total = (int)ms.Length;
                    int payload = total - 4;
                    buf[0] = (byte)(payload >> 24); buf[1] = (byte)(payload >> 16);
                    buf[2] = (byte)(payload >> 8); buf[3] = (byte)payload;

                    if (SameAsPrev(buf, total, prev, prevLen))
                    {
                        // картинка не змінилась - не вантажимо планшет
                        if (lastSend.ElapsedMilliseconds > 1500)
                        {
                            ns.Write(keepAlive, 0, 4);
                            lastSend.Restart();
                        }
                    }
                    else
                    {
                        credit.Wait(1500); // чекаємо підтвердження попереднього кадру
                        ns.Write(buf, 0, total);
                        lastSend.Restart();
                        if (prev.Length < total) prev = new byte[total + 65536];
                        Buffer.BlockCopy(buf, 0, prev, 0, total);
                        prevLen = total;
                    }

                    int wait = 1000 / Math.Max(1, fps) - (int)sw.ElapsedMilliseconds;
                    if (wait > 0) Thread.Sleep(wait);
                }
            }
            finally
            {
                sess.Alive = false;
                if (sess.LeftDown) Native.mouse_event(Native.LEFTUP, 0, 0, 0, UIntPtr.Zero);
                if (src != null) src.Dispose();
                if (dst != null) dst.Dispose();
                ms.Dispose();
            }
        }

        static bool SameAsPrev(byte[] a, int alen, byte[] b, int blen)
        {
            if (alen != blen) return false;
            for (int i = 4; i < alen; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static ImageCodecInfo GetJpegCodec()
        {
            foreach (ImageCodecInfo ci in ImageCodecInfo.GetImageEncoders())
                if (ci.MimeType == "image/jpeg") return ci;
            throw new Exception("JPEG кодек не знайдено");
        }

        static void DrawCursor(Graphics g, Rectangle bounds)
        {
            try
            {
                Native.CURSORINFO ci = new Native.CURSORINFO();
                ci.cbSize = Marshal.SizeOf(typeof(Native.CURSORINFO));
                if (!Native.GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;

                int hx = 0, hy = 0;
                Native.ICONINFO ii;
                if (Native.GetIconInfo(ci.hCursor, out ii))
                {
                    hx = ii.xHotspot; hy = ii.yHotspot;
                    if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
                }

                IntPtr hdc = g.GetHdc();
                try
                {
                    Native.DrawIconEx(hdc, ci.pt.x - bounds.X - hx, ci.pt.y - bounds.Y - hy, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3);
                }
                finally { g.ReleaseHdc(hdc); }
            }
            catch { }
        }

        // ---------- події від планшета ----------

        void ReadEvents(NetworkStream ns, Session sess, SemaphoreSlim credit)
        {
            try
            {
                while (sess.Alive)
                {
                    byte[] m = ReadExact(ns, 5);
                    if (m == null) break;
                    int type = m[0];
                    int nx = (m[1] << 8) | m[2];
                    int ny = (m[3] << 8) | m[4];

                    if (type == 0)
                    {
                        try { if (credit.CurrentCount < 2) credit.Release(); } catch (SemaphoreFullException) { }
                        continue;
                    }
                    if (!touchEnabled) continue;

                    Rectangle b = GetBounds(selScreen);
                    int x = b.X + (int)((long)nx * (b.Width - 1) / 65535);
                    int y = b.Y + (int)((long)ny * (b.Height - 1) / 65535);

                    switch (type)
                    {
                        case 1:
                            Native.SetCursorPos(x, y);
                            Native.mouse_event(Native.LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                            sess.LeftDown = true;
                            break;
                        case 2:
                            Native.SetCursorPos(x, y);
                            break;
                        case 3:
                            Native.SetCursorPos(x, y);
                            if (sess.LeftDown)
                            {
                                Native.mouse_event(Native.LEFTUP, 0, 0, 0, UIntPtr.Zero);
                                sess.LeftDown = false;
                            }
                            break;
                        case 4:
                            Native.SetCursorPos(x, y);
                            Native.mouse_event(Native.RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                            Native.mouse_event(Native.RIGHTUP, 0, 0, 0, UIntPtr.Zero);
                            break;
                    }
                }
            }
            catch { }
            sess.Alive = false;
        }
    }
}
