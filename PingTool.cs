using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

// ── Main monitor window ──────────────────────────────────────────────────────

public class PingTool : Form
{
    class DeviceStats
    {
        public readonly object Lock = new object();
        public string IP;
        public int    Sent, Received;
        public long   RttSum;
        public bool   LastOnline;
        public volatile bool StopFlag;
        public Thread PingThread;

        public bool Running { get { return PingThread != null && PingThread.IsAlive; } }
        public int  Loss    { get { return Sent > 0 ? (int)Math.Round((Sent - Received) * 100.0 / Sent) : 0; } }
        public long AvgRtt  { get { return Received > 0 ? RttSum / Received : 0; } }
    }

    private readonly List<DeviceStats> devices = new List<DeviceStats>();
    private System.Windows.Forms.Timer refreshTimer;
    private static readonly object _logLock = new object();

    private TextBox      ipEntry;
    private Button       addBtn, removeBtn, scanWindowBtn;
    private DataGridView grid;
    private TextBox      timeoutEntry;
    private TextBox      sizeEntry;
    private CheckBox     dontFragCheck;
    private CheckBox     logMissedCheck;
    private TrackBar     intervalSlider;
    private Label        intervalValueLabel;
    private ComboBox     countCombo;
    private CheckBox     unlimitedCheck;
    private Button       startBtn, stopBtn, clearBtn;
    private Label        statusLabel;

    private static readonly Color BgDark    = Color.FromArgb(17,  17,  27);
    private static readonly Color BgMid     = Color.FromArgb(30,  30,  46);
    private static readonly Color BgPanel   = Color.FromArgb(49,  50,  68);
    private static readonly Color BgAlt     = Color.FromArgb(24,  24,  37);
    private static readonly Color FgText    = Color.FromArgb(205, 214, 244);
    private static readonly Color FgDim     = Color.FromArgb(166, 173, 200);
    private static readonly Color ColBlue   = Color.FromArgb(137, 180, 250);
    private static readonly Color ColGreen  = Color.FromArgb(166, 227, 161);
    private static readonly Color ColRed    = Color.FromArgb(243, 139, 168);
    private static readonly Color ColOrange = Color.FromArgb(250, 179, 135);
    private static readonly Color ColYellow = Color.FromArgb(249, 226, 175);

    public PingTool()
    {
        BuildUI();
        LoadSettings();
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        refreshTimer = new System.Windows.Forms.Timer { Interval = 500 };
        refreshTimer.Tick += (s, e) => RefreshGrid();
        refreshTimer.Start();
        FormClosing += (s, e) => { SaveSettings(); foreach (var d in devices) d.StopFlag = true; };
    }

    private void BuildUI()
    {
        Text            = "Net View Systems Network Tool";
        Size            = new Size(700, 622);
        MinimumSize     = new Size(600, 482);
        BackColor       = BgMid;
        Font            = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.Sizable;

        Controls.Add(new Label {
            Text = "Net View Systems Network Tool", ForeColor = ColBlue, BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold), AutoSize = true,
            Location = new Point(20, 14)
        });

        // ── Row 1: IP entry + Add + Remove + Network Scan ────
        int y = 52;
        Controls.Add(MakeLabel("IPv4 Address:", 20, y));

        ipEntry = new TextBox {
            Location    = new Point(130, y - 2), Size = new Size(155, 26),
            BackColor   = BgPanel, ForeColor = FgText,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 11f)
        };
        ipEntry.KeyDown += (s, e) => { if (e.KeyCode == Keys.Return) AddDevice(); };
        Controls.Add(ipEntry);

        addBtn = MakeButton("Add Device", 296, y - 2, ColBlue, BgDark, 100);
        addBtn.Click += (s, e) => AddDevice();
        Controls.Add(addBtn);

        removeBtn = MakeButton("Remove", 406, y - 2, ColRed, BgDark, 80);
        removeBtn.Click += (s, e) => RemoveSelected();
        Controls.Add(removeBtn);

        scanWindowBtn = MakeButton("Network Scan", 496, y - 2, ColOrange, BgDark, 116);
        scanWindowBtn.Click += (s, e) =>
            new NetworkScanForm(ip => AddDeviceByIP(ip)).Show(this);
        Controls.Add(scanWindowBtn);

        // ── Row 2: Timeout + Interval ────────────────────────
        y = 92;
        Controls.Add(MakeLabel("Timeout (ms):", 20, y));

        timeoutEntry = new TextBox {
            Location    = new Point(130, y - 2), Size = new Size(70, 26),
            BackColor   = BgPanel, ForeColor = FgText,
            BorderStyle = BorderStyle.FixedSingle, Text = "1000"
        };
        Controls.Add(timeoutEntry);

        Controls.Add(MakeLabel("Interval (ms):", 220, y));

        intervalSlider = new TrackBar {
            Location      = new Point(330, y - 6), Size = new Size(230, 40),
            Minimum       = 250, Maximum = 2000, Value = 1000,
            TickFrequency = 250, LargeChange = 250, SmallChange = 50,
            BackColor     = BgMid
        };
        intervalValueLabel = new Label {
            Text = "1000 ms", ForeColor = ColOrange, BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold), AutoSize = true,
            Location = new Point(568, y + 4)
        };
        intervalSlider.ValueChanged += (s, e) =>
            intervalValueLabel.Text = intervalSlider.Value + " ms";
        Controls.Add(intervalSlider);
        Controls.Add(intervalValueLabel);

        // ── Row 3: Size ──────────────────────────────────────
        y = 134;
        Controls.Add(MakeLabel("Size (bytes):", 20, y));

        sizeEntry = new TextBox {
            Location    = new Point(130, y - 2), Size = new Size(70, 26),
            BackColor   = BgPanel, ForeColor = FgText,
            BorderStyle = BorderStyle.FixedSingle, Text = "32"
        };
        Controls.Add(sizeEntry);

        Controls.Add(new Label {
            Text = "(32 – 65535)", ForeColor = FgDim, BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9f), AutoSize = true,
            Location = new Point(210, y + 4)
        });

        dontFragCheck = new CheckBox {
            Text = "Don't Fragment (-f)", ForeColor = FgText, BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 10f), AutoSize = true,
            Location = new Point(330, y + 2), Cursor = Cursors.Hand
        };
        Controls.Add(dontFragCheck);

        logMissedCheck = new CheckBox {
            Text = "Log missed pings", ForeColor = FgText, BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 10f), AutoSize = true, Checked = true,
            Location = new Point(510, y + 2), Cursor = Cursors.Hand
        };
        Controls.Add(logMissedCheck);

        // ── Row 4: Count + Unlimited + Action buttons ────────
        y = 176;
        Controls.Add(MakeLabel("Count:", 20, y));

        countCombo = new ComboBox {
            Location      = new Point(76, y - 2), Size = new Size(76, 26),
            BackColor     = BgPanel, ForeColor = FgText,
            FlatStyle     = FlatStyle.Flat, DropDownStyle = ComboBoxStyle.DropDownList
        };
        countCombo.Items.AddRange(new object[] { "4", "10", "20", "50", "100" });
        countCombo.SelectedIndex = 0;
        Controls.Add(countCombo);

        unlimitedCheck = new CheckBox {
            Text = "Unlimited", ForeColor = ColOrange, BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 10f), AutoSize = true,
            Location = new Point(164, y + 2), Cursor = Cursors.Hand
        };
        unlimitedCheck.CheckedChanged += (s, e) => countCombo.Enabled = !unlimitedCheck.Checked;
        Controls.Add(unlimitedCheck);

        startBtn = MakeButton("Start All", 294, y - 2, ColGreen, BgDark, 88);
        startBtn.Click += (s, e) => StartAll();
        Controls.Add(startBtn);

        stopBtn = MakeButton("Stop All", 392, y - 2, ColRed, BgDark, 84);
        stopBtn.Enabled = false;
        stopBtn.Click += (s, e) => StopAll();
        Controls.Add(stopBtn);

        clearBtn = MakeButton("Clear Stats", 486, y - 2, FgDim, BgDark, 96);
        clearBtn.Click += (s, e) => ClearStats();
        Controls.Add(clearBtn);

        // ── Status bar ───────────────────────────────────────
        statusLabel = new Label {
            Text = "No devices added.", ForeColor = FgDim, BackColor = BgDark,
            Font = new Font("Segoe UI", 9f), TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            Location = new Point(20, 214), Size = new Size(656, 22)
        };
        Controls.Add(statusLabel);

        // ── Device grid ──────────────────────────────────────
        grid = new DataGridView {
            Location                    = new Point(20, 242),
            Size                        = new Size(656, 326),
            BackgroundColor             = BgDark,
            ForeColor                   = FgText,
            GridColor                   = BgPanel,
            BorderStyle                 = BorderStyle.None,
            RowHeadersVisible           = false,
            AllowUserToAddRows          = false,
            AllowUserToDeleteRows       = false,
            AllowUserToResizeRows       = false,
            ReadOnly                    = true,
            SelectionMode               = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect                 = false,
            Font                        = new Font("Consolas", 10f),
            AutoSizeColumnsMode         = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight         = 30,
            RowTemplate                 = { Height = 26 }
        };

        grid.DefaultCellStyle                        = MakeCellStyle(BgDark, FgText, BgPanel);
        grid.AlternatingRowsDefaultCellStyle         = MakeCellStyle(BgAlt,  FgText, BgPanel);
        grid.ColumnHeadersDefaultCellStyle.BackColor = BgPanel;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = ColBlue;
        grid.ColumnHeadersDefaultCellStyle.Font      = new Font("Segoe UI", 10f, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
        grid.EnableHeadersVisualStyles               = false;

        AddCol(grid, "IP Address", "ip",       160);
        AddCol(grid, "Status",     "status",    90);
        AddCol(grid, "Sent",       "sent",      80);
        AddCol(grid, "Received",   "received",  80);
        AddCol(grid, "Loss %",     "loss",      80);
        AddCol(grid, "Avg RTT",    "avgrtt",    90);

        grid.CellFormatting += OnCellFormatting;
        Controls.Add(grid);
        Resize += (s, e) => RelayoutDynamic();
    }

    private DataGridViewCellStyle MakeCellStyle(Color back, Color fore, Color selBack)
    {
        return new DataGridViewCellStyle {
            BackColor = back, ForeColor = fore,
            SelectionBackColor = selBack, SelectionForeColor = fore,
            Alignment = DataGridViewContentAlignment.MiddleCenter
        };
    }

    internal static void AddCol(DataGridView g, string header, string name, int weight)
    {
        g.Columns.Add(new DataGridViewTextBoxColumn {
            HeaderText = header, Name = name,
            FillWeight = weight, MinimumWidth = 60,
            DefaultCellStyle = { Alignment = DataGridViewContentAlignment.MiddleCenter }
        });
    }

    private void OnCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.ColumnIndex != 1 || e.Value == null) return;
        switch (e.Value.ToString())
        {
            case "Online":  e.CellStyle.ForeColor = ColGreen;  break;
            case "Offline": e.CellStyle.ForeColor = ColRed;    break;
            default:        e.CellStyle.ForeColor = ColYellow; break;
        }
    }

    private void RelayoutDynamic()
    {
        int w = ClientSize.Width - 40;
        if (w < 100) return;
        if (grid        != null) { grid.Width = w; grid.Height = ClientSize.Height - 276; }
        if (statusLabel != null)   statusLabel.Width = w;
    }

    private Label MakeLabel(string text, int x, int y)
    {
        return new Label {
            Text = text, ForeColor = FgText, BackColor = Color.Transparent,
            AutoSize = true, Location = new Point(x, y + 4)
        };
    }

    private Button MakeButton(string text, int x, int y, Color bg, Color fg, int width = 72)
    {
        var b = new Button {
            Text = text, Location = new Point(x, y), Size = new Size(width, 30),
            BackColor = bg, ForeColor = fg, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold), Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    // ── Device management ────────────────────────────────────────────────────

    private bool ValidateIP(string ip)
    {
        if (!Regex.IsMatch(ip, @"^(\d{1,3}\.){3}\d{1,3}$")) return false;
        foreach (var part in ip.Split('.'))
            if (int.Parse(part) > 255) return false;
        return true;
    }

    private void AddDevice()
    {
        AddDeviceByIP(ipEntry.Text.Trim());
        ipEntry.Focus();
    }

    internal void AddDeviceByIP(string ip)
    {
        if (!ValidateIP(ip)) { statusLabel.Text = "Invalid IPv4 address: " + ip; return; }
        foreach (var d in devices)
            if (d.IP == ip) { statusLabel.Text = ip + " is already in the list."; return; }

        var stats = new DeviceStats { IP = ip };
        devices.Add(stats);
        grid.Rows.Add(ip, "Waiting...", "—", "—", "—", "—");

        LaunchDevice(stats);
        SetControlState(false);
        statusLabel.Text = "Monitoring " + devices.Count + " device(s)...";
    }

    private void LaunchDevice(DeviceStats d)
    {
        int timeout;
        if (!int.TryParse(timeoutEntry.Text.Trim(), out timeout) || timeout < 1) timeout = 1000;
        int size;
        if (!int.TryParse(sizeEntry.Text.Trim(), out size) || size < 32 || size > 65535) size = 32;
        bool dontFrag   = dontFragCheck.Checked;
        bool logMissed  = logMissedCheck.Checked;
        bool continuous = unlimitedCheck.Checked;
        int  count      = continuous ? 0 : int.Parse(countCombo.SelectedItem.ToString());
        int  interval   = intervalSlider.Value;

        d.StopFlag   = false;
        var cap      = d;
        d.PingThread = new Thread(() => PingLoop(cap, count, continuous, timeout, interval, size, dontFrag, logMissed))
            { IsBackground = true };
        d.PingThread.Start();
    }

    private void RemoveSelected()
    {
        if (grid.SelectedRows.Count == 0) { statusLabel.Text = "Select a device to remove."; return; }
        int idx = grid.SelectedRows[0].Index;
        if (idx < 0 || idx >= devices.Count) return;
        var d = devices[idx];
        d.StopFlag = true;
        devices.RemoveAt(idx);
        grid.Rows.RemoveAt(idx);
        statusLabel.Text = d.IP + " removed.";
    }

    // ── Start / Stop ─────────────────────────────────────────────────────────

    private void StartAll()
    {
        if (devices.Count == 0) { statusLabel.Text = "Add at least one device first."; return; }
        foreach (var d in devices)
            if (!d.Running) LaunchDevice(d);
        SetControlState(false);
        statusLabel.Text = "Monitoring " + devices.Count + " device(s)...";
    }

    private void StopAll()
    {
        foreach (var d in devices) d.StopFlag = true;
        SetControlState(true);
        statusLabel.Text = "Stopped.";
    }

    private void ClearStats()
    {
        foreach (var d in devices)
            if (d.Running) { statusLabel.Text = "Stop monitoring before clearing stats."; return; }
        foreach (var d in devices)
            lock (d.Lock) { d.Sent = 0; d.Received = 0; d.RttSum = 0; d.LastOnline = false; }
        RefreshGrid();
        statusLabel.Text = "Stats cleared.";
    }

    private void SetControlState(bool ready)
    {
        startBtn.Enabled       = ready;
        stopBtn.Enabled        = !ready;
        timeoutEntry.Enabled   = ready;
        sizeEntry.Enabled      = ready;
        dontFragCheck.Enabled  = ready;
        logMissedCheck.Enabled = ready;
        intervalSlider.Enabled = ready;
        unlimitedCheck.Enabled = ready;
        countCombo.Enabled     = ready && !unlimitedCheck.Checked;
        ipEntry.Enabled        = true;
        addBtn.Enabled         = true;
        removeBtn.Enabled      = true;
        scanWindowBtn.Enabled  = true;
    }

    // ── Missed-ping log ──────────────────────────────────────────────────────

    private static void LogMissedPing(string ip)
    {
        try
        {
            string date    = DateTime.Now.ToString("yyyy-MM-dd");
            string time    = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string dir     = Path.GetDirectoryName(Application.ExecutablePath);
            string path    = Path.Combine(dir, "missed_pings_" + date + ".csv");
            lock (_logLock)
            {
                bool newFile = !File.Exists(path);
                using (var sw = new StreamWriter(path, append: true))
                {
                    if (newFile) sw.WriteLine("Timestamp,IP Address");
                    sw.WriteLine(time + "," + ip);
                }
            }
        }
        catch { }
    }

    // ── Ping loop ────────────────────────────────────────────────────────────

    private void PingLoop(DeviceStats d, int count, bool continuous, int timeout, int interval, int size, bool dontFrag, bool logMissed)
    {
        var    pinger  = new Ping();
        var    options = new PingOptions { DontFragment = dontFrag };
        byte[] buffer  = new byte[size];
        int    seq     = 0;

        while (!d.StopFlag)
        {
            seq++;
            try
            {
                PingReply reply = pinger.Send(d.IP, timeout, buffer, options);
                lock (d.Lock)
                {
                    d.Sent++;
                    if (reply.Status == IPStatus.Success)
                    {
                        d.Received++;
                        d.RttSum    += reply.RoundtripTime;
                        d.LastOnline = true;
                    }
                    else { d.LastOnline = false; }
                }
                if (!d.LastOnline && logMissed) LogMissedPing(d.IP);
            }
            catch { lock (d.Lock) { d.Sent++; d.LastOnline = false; } if (logMissed) LogMissedPing(d.IP); }

            if (!continuous && seq >= count) { d.StopFlag = true; break; }

            int slept = 0;
            while (slept < interval && !d.StopFlag)
            {
                Thread.Sleep(Math.Min(50, interval - slept));
                slept += 50;
            }
        }
    }

    // ── Grid refresh ─────────────────────────────────────────────────────────

    private void RefreshGrid()
    {
        for (int i = 0; i < devices.Count && i < grid.Rows.Count; i++)
        {
            var  d = devices[i];
            int  sent, received, loss;
            long avgRtt;
            bool online;

            lock (d.Lock)
            {
                sent     = d.Sent;
                received = d.Received;
                loss     = d.Loss;
                avgRtt   = d.AvgRtt;
                online   = d.LastOnline;
            }

            var row = grid.Rows[i];
            row.Cells["status"].Value   = sent == 0 ? "Waiting..." : online ? "Online" : "Offline";
            row.Cells["sent"].Value     = sent > 0     ? sent.ToString()     : "—";
            row.Cells["received"].Value = sent > 0     ? received.ToString() : "—";
            row.Cells["loss"].Value     = sent > 0     ? loss + "%"          : "—";
            row.Cells["avgrtt"].Value   = received > 0 ? avgRtt + " ms"      : "—";
        }

        if (!startBtn.Enabled && devices.Count > 0)
        {
            bool anyRunning = false;
            foreach (var d in devices) if (d.Running) { anyRunning = true; break; }
            if (!anyRunning) { SetControlState(true); statusLabel.Text = "Monitoring complete."; }
        }
    }

    // ── Window size persistence ───────────────────────────────────────────────

    private void SaveSettings()
    {
        try
        {
            string path   = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "settings.cfg");
            var    bounds = WindowState == FormWindowState.Normal
                ? new Rectangle(Left, Top, Width, Height)
                : RestoreBounds;
            File.WriteAllText(path,
                "Width="  + bounds.Width  + "\r\n" +
                "Height=" + bounds.Height + "\r\n" +
                "State="  + WindowState   + "\r\n");
        }
        catch { }
    }

    private void LoadSettings()
    {
        try
        {
            string path = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "settings.cfg");
            if (!File.Exists(path)) return;

            var vals = new Dictionary<string, string>();
            foreach (var line in File.ReadAllLines(path))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) vals[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }

            int w, h;
            if (vals.ContainsKey("Width")  && int.TryParse(vals["Width"],  out w) &&
                vals.ContainsKey("Height") && int.TryParse(vals["Height"], out h))
            {
                w = Math.Max(MinimumSize.Width,  Math.Min(w, Screen.PrimaryScreen.WorkingArea.Width));
                h = Math.Max(MinimumSize.Height, Math.Min(h, Screen.PrimaryScreen.WorkingArea.Height));
                Size = new Size(w, h);
            }

            string state;
            if (vals.TryGetValue("State", out state) && state == "Maximized")
                WindowState = FormWindowState.Maximized;
        }
        catch { }
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new PingTool());
    }
}

// ── Network scan window ──────────────────────────────────────────────────────

public class NetworkScanForm : Form
{
    private TextBox      cidrEntry;
    private Label        rangeLabel;
    private Button       scanBtn, cancelBtn;
    private ProgressBar  progressBar;
    private Label        progressLabel;
    private Label        statusLabel;
    private DataGridView resultsGrid;
    private Button       selectAllBtn, selectRespondedBtn, clearSelBtn, addBtn;

    private volatile bool cancelFlag;
    private readonly Action<string> addCallback;

    private static readonly Color BgDark    = Color.FromArgb(17,  17,  27);
    private static readonly Color BgMid     = Color.FromArgb(30,  30,  46);
    private static readonly Color BgPanel   = Color.FromArgb(49,  50,  68);
    private static readonly Color BgAlt     = Color.FromArgb(24,  24,  37);
    private static readonly Color FgText    = Color.FromArgb(205, 214, 244);
    private static readonly Color FgDim     = Color.FromArgb(166, 173, 200);
    private static readonly Color ColBlue   = Color.FromArgb(137, 180, 250);
    private static readonly Color ColGreen  = Color.FromArgb(166, 227, 161);
    private static readonly Color ColRed    = Color.FromArgb(243, 139, 168);
    private static readonly Color ColOrange = Color.FromArgb(250, 179, 135);

    public NetworkScanForm(Action<string> addCallback)
    {
        this.addCallback = addCallback;
        BuildUI();
    }

    private void BuildUI()
    {
        Text            = "Network Scan";
        Size            = new Size(540, 600);
        MinimumSize     = new Size(420, 460);
        BackColor       = BgMid;
        Font            = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.Sizable;

        Controls.Add(new Label {
            Text = "Network Scan", ForeColor = ColBlue, BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold), AutoSize = true,
            Location = new Point(20, 14)
        });

        // ── CIDR input ───────────────────────────────────────
        int y = 52;
        Controls.Add(MakeLabel("CIDR Range:", 20, y));

        cidrEntry = new TextBox {
            Location    = new Point(112, y - 2), Size = new Size(155, 26),
            BackColor   = BgPanel, ForeColor = FgText,
            BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 11f),
            Text        = "192.168.1.0/24"
        };
        cidrEntry.TextChanged += (s, e) => UpdateRangeLabel();
        cidrEntry.KeyDown     += (s, e) => { if (e.KeyCode == Keys.Return) StartScan(); };
        Controls.Add(cidrEntry);

        rangeLabel = new Label {
            Text = "→ 254 hosts", ForeColor = ColOrange, BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold), AutoSize = true,
            Location = new Point(276, y + 4)
        };
        Controls.Add(rangeLabel);

        // ── Buttons ──────────────────────────────────────────
        y = 88;
        scanBtn = MakeButton("Scan", 20, y, ColBlue, BgDark, 80);
        scanBtn.Click += (s, e) => StartScan();
        Controls.Add(scanBtn);

        cancelBtn = MakeButton("Cancel", 110, y, ColRed, BgDark, 80);
        cancelBtn.Enabled = false;
        cancelBtn.Click += (s, e) => { cancelFlag = true; statusLabel.Text = "Cancelling..."; };
        Controls.Add(cancelBtn);

        // ── Progress ─────────────────────────────────────────
        y = 130;
        progressBar = new ProgressBar {
            Location = new Point(20, y), Size = new Size(360, 16),
            Minimum = 0, Maximum = 100, Value = 0,
            Style = ProgressBarStyle.Continuous
        };
        Controls.Add(progressBar);

        progressLabel = new Label {
            Text = "", ForeColor = FgDim, BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 9f), AutoSize = true,
            Location = new Point(388, y)
        };
        Controls.Add(progressLabel);

        // ── Status bar ───────────────────────────────────────
        statusLabel = new Label {
            Text = "Enter a CIDR range and click Scan.",
            ForeColor = FgDim, BackColor = BgDark,
            Font = new Font("Segoe UI", 9f), TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            Location = new Point(20, 154), Size = new Size(496, 22)
        };
        Controls.Add(statusLabel);

        // ── Results grid ─────────────────────────────────────
        resultsGrid = new DataGridView {
            Location                    = new Point(20, 182),
            Size                        = new Size(496, 310),
            BackgroundColor             = BgDark,
            ForeColor                   = FgText,
            GridColor                   = BgPanel,
            BorderStyle                 = BorderStyle.None,
            RowHeadersVisible           = false,
            AllowUserToAddRows          = false,
            AllowUserToDeleteRows       = false,
            AllowUserToResizeRows       = false,
            ReadOnly                    = false,
            SelectionMode               = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect                 = true,
            Font                        = new Font("Consolas", 10f),
            AutoSizeColumnsMode         = DataGridViewAutoSizeColumnsMode.Fill,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            ColumnHeadersHeight         = 28,
            RowTemplate                 = { Height = 24 }
        };

        resultsGrid.DefaultCellStyle                        = MakeCellStyle(BgDark, FgText, BgPanel);
        resultsGrid.AlternatingRowsDefaultCellStyle         = MakeCellStyle(BgAlt,  FgText, BgPanel);
        resultsGrid.ColumnHeadersDefaultCellStyle.BackColor = BgPanel;
        resultsGrid.ColumnHeadersDefaultCellStyle.ForeColor = ColBlue;
        resultsGrid.ColumnHeadersDefaultCellStyle.Font      = new Font("Segoe UI", 10f, FontStyle.Bold);
        resultsGrid.EnableHeadersVisualStyles               = false;

        // Checkbox column
        var chkCol = new DataGridViewCheckBoxColumn {
            HeaderText = "", Name = "select",
            FillWeight = 28, MinimumWidth = 28, Width = 28,
            ReadOnly   = false
        };
        chkCol.DefaultCellStyle.BackColor          = BgDark;
        chkCol.DefaultCellStyle.SelectionBackColor = BgPanel;
        resultsGrid.Columns.Add(chkCol);

        PingTool.AddCol(resultsGrid, "IP Address",  "ip",      150);
        PingTool.AddCol(resultsGrid, "Status",      "status",  100);
        PingTool.AddCol(resultsGrid, "RTT",         "rtt",      60);
        PingTool.AddCol(resultsGrid, "MAC Address", "mac",     150);

        resultsGrid.CellFormatting  += OnResultCellFormatting;
        // Commit checkbox on single click rather than waiting for focus-leave
        resultsGrid.CellContentClick += (s, e) => {
            if (e.ColumnIndex == 0 && e.RowIndex >= 0)
                resultsGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        Controls.Add(resultsGrid);

        // ── Selection helpers ────────────────────────────────
        y = 500;
        selectAllBtn = MakeButton("Select All", 20, y, FgDim, BgDark, 96);
        selectAllBtn.Click += (s, e) => SetAllChecked(true);
        Controls.Add(selectAllBtn);

        selectRespondedBtn = MakeButton("Responded Only", 126, y, ColGreen, BgDark, 130);
        selectRespondedBtn.Click += (s, e) => SelectResponded();
        Controls.Add(selectRespondedBtn);

        clearSelBtn = MakeButton("Clear", 266, y, ColRed, BgDark, 70);
        clearSelBtn.Click += (s, e) => SetAllChecked(false);
        Controls.Add(clearSelBtn);

        addBtn = MakeButton("Add Selected to Monitor", 20, y + 38, ColBlue, BgDark, 210);
        addBtn.Click += (s, e) => AddSelected();
        Controls.Add(addBtn);

        Resize += (s, e) => RelayoutDynamic();
        FormClosing += (s, e) => cancelFlag = true;
    }

    private void OnResultCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.ColumnIndex != 2 || e.Value == null) return;
        e.CellStyle.ForeColor = e.Value.ToString() == "Responded" ? ColGreen : ColRed;
    }

    private void UpdateRangeLabel()
    {
        try
        {
            long count = CalculateHostCount(cidrEntry.Text.Trim());
            rangeLabel.Text      = "→ " + count + " host" + (count == 1 ? "" : "s");
            rangeLabel.ForeColor = count > 4094 ? ColRed : ColOrange;
        }
        catch { rangeLabel.Text = ""; }
    }

    // ── MAC address lookup via ARP ────────────────────────────────────────────

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern int SendARP(int destIP, int srcIP, byte[] macAddr, ref int phyAddrLen);

    private static string GetMacAddress(string ip)
    {
        try
        {
            byte[] ipBytes = IPAddress.Parse(ip).GetAddressBytes();
            int    destIP  = BitConverter.ToInt32(ipBytes, 0);
            byte[] mac     = new byte[6];
            int    len     = mac.Length;
            if (SendARP(destIP, 0, mac, ref len) == 0 && len == 6)
                return string.Format("{0:X2}-{1:X2}-{2:X2}-{3:X2}-{4:X2}-{5:X2}",
                    mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
        }
        catch { }
        return "—";
    }

    // Counts hosts mathematically — never builds the list, safe to call on every keystroke
    private static long CalculateHostCount(string cidr)
    {
        var parts = cidr.Trim().Split('/');
        if (parts.Length != 2) throw new FormatException();
        var octets = parts[0].Trim().Split('.');
        if (octets.Length != 4) throw new FormatException();
        foreach (var o in octets) { int v = int.Parse(o); if (v < 0 || v > 255) throw new FormatException(); }
        int prefix = int.Parse(parts[1].Trim());
        if (prefix < 1 || prefix > 32) throw new FormatException();
        if (prefix == 32) return 1;
        if (prefix == 31) return 2;
        return ((long)1 << (32 - prefix)) - 2;
    }

    // ── Scan ─────────────────────────────────────────────────────────────────

    private void StartScan()
    {
        long hostCount;
        try   { hostCount = CalculateHostCount(cidrEntry.Text.Trim()); }
        catch { statusLabel.Text = "Invalid CIDR notation."; return; }

        if (hostCount == 0)     { statusLabel.Text = "No hosts in range."; return; }
        if (hostCount > 65534)  { statusLabel.Text = "Range too large (max /16, 65534 hosts)."; return; }

        List<string> ips;
        try   { ips = GetIPsFromCIDR(cidrEntry.Text.Trim()); }
        catch (Exception ex) { statusLabel.Text = "Invalid CIDR: " + ex.Message; return; }

        resultsGrid.Rows.Clear();
        progressBar.Maximum = ips.Count;
        progressBar.Value   = 0;
        progressLabel.Text  = "";
        cancelFlag          = false;

        scanBtn.Enabled   = false;
        cancelBtn.Enabled = true;
        statusLabel.Text  = "Scanning " + ips.Count + " hosts...";

        new Thread(() => RunScan(ips)) { IsBackground = true }.Start();
    }

    private void RunScan(List<string> ips)
    {
        int total     = ips.Count;
        int completed = 0;
        int nThreads  = Math.Min(64, total);
        var threads   = new Thread[nThreads];

        for (int t = 0; t < nThreads; t++)
        {
            int ti = t;
            threads[t] = new Thread(() =>
            {
                var pinger = new Ping();
                for (int i = ti; i < total && !cancelFlag; i += nThreads)
                {
                    string ip        = ips[i];
                    bool   responded = false;
                    long   rtt       = 0;
                    string mac       = "—";
                    try
                    {
                        PingReply reply = pinger.Send(ip, 500);
                        responded = reply.Status == IPStatus.Success;
                        if (responded)
                        {
                            rtt = reply.RoundtripTime;
                            mac = GetMacAddress(ip);
                        }
                    }
                    catch { }

                    int done = Interlocked.Increment(ref completed);
                    AddResult(ip, responded, rtt, mac, done, total);
                }
            }) { IsBackground = true };
            threads[t].Start();
        }

        foreach (var t in threads) t.Join();

        if (!IsDisposed)
            Invoke(new Action(() => OnScanFinished(completed, total)));
    }

    private void AddResult(string ip, bool responded, long rtt, string mac, int done, int total)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { Invoke(new Action(() => AddResult(ip, responded, rtt, mac, done, total))); return; }

        resultsGrid.Rows.Add(false, ip,
            responded ? "Responded" : "No Response",
            responded ? rtt + " ms" : "—",
            mac);

        progressBar.Value  = Math.Min(done, progressBar.Maximum);
        progressLabel.Text = done + " / " + total;
    }

    private void OnScanFinished(int completed, int total)
    {
        SortResultsByIP();

        scanBtn.Enabled   = true;
        cancelBtn.Enabled = false;

        int responded = 0;
        foreach (DataGridViewRow row in resultsGrid.Rows)
            if (row.Cells["status"].Value != null &&
                row.Cells["status"].Value.ToString() == "Responded") responded++;

        statusLabel.Text = (cancelFlag ? "Scan cancelled. " : "Scan complete. ") +
            completed + " hosts scanned, " + responded + " responded.";
    }

    private void SortResultsByIP()
    {
        var rows = new List<object[]>();
        foreach (DataGridViewRow row in resultsGrid.Rows)
            rows.Add(new object[] {
                row.Cells["select"].Value,
                row.Cells["ip"].Value,
                row.Cells["status"].Value,
                row.Cells["rtt"].Value,
                row.Cells["mac"].Value
            });

        rows.Sort((a, b) => {
            var pa = a[1] != null ? a[1].ToString().Split('.') : new string[0];
            var pb = b[1] != null ? b[1].ToString().Split('.') : new string[0];
            if (pa.Length < 4 || pb.Length < 4) return 0;
            int c = int.Parse(pa[2]).CompareTo(int.Parse(pb[2]));
            return c != 0 ? c : int.Parse(pa[3]).CompareTo(int.Parse(pb[3]));
        });

        resultsGrid.Rows.Clear();
        foreach (var r in rows)
            resultsGrid.Rows.Add(r);
    }

    // ── Selection helpers ─────────────────────────────────────────────────────

    private void SetAllChecked(bool value)
    {
        foreach (DataGridViewRow row in resultsGrid.Rows)
            row.Cells["select"].Value = value;
    }

    private void SelectResponded()
    {
        foreach (DataGridViewRow row in resultsGrid.Rows)
            row.Cells["select"].Value =
                row.Cells["status"].Value != null &&
                row.Cells["status"].Value.ToString() == "Responded";
    }

    private void AddSelected()
    {
        int count = 0;
        foreach (DataGridViewRow row in resultsGrid.Rows)
        {
            if (row.Cells["select"].Value is bool && (bool)row.Cells["select"].Value)
            {
                addCallback(row.Cells["ip"].Value.ToString());
                count++;
            }
        }
        statusLabel.Text = count > 0
            ? count + " device(s) sent to monitor."
            : "No devices selected.";
    }

    // ── CIDR parser ───────────────────────────────────────────────────────────

    private static List<string> GetIPsFromCIDR(string cidr)
    {
        var parts = cidr.Trim().Split('/');
        if (parts.Length != 2) throw new FormatException("Expected x.x.x.x/n");

        var octets = parts[0].Trim().Split('.');
        if (octets.Length != 4) throw new FormatException("Invalid IP address");

        int prefix = int.Parse(parts[1].Trim());
        if (prefix < 1 || prefix > 32) throw new FormatException("Prefix must be 1–32");

        uint ip = 0;
        for (int i = 0; i < 4; i++)
        {
            int o = int.Parse(octets[i]);
            if (o < 0 || o > 255) throw new FormatException("Octet out of range");
            ip = (ip << 8) | (uint)o;
        }

        if (prefix == 32)
        {
            var single = new List<string>();
            single.Add(parts[0].Trim());
            return single;
        }

        uint mask      = 0xFFFFFFFFu << (32 - prefix);
        uint network   = ip & mask;
        uint broadcast = network | ~mask;

        var result = new List<string>();
        for (uint addr = network + 1; addr < broadcast; addr++)
            result.Add(string.Format("{0}.{1}.{2}.{3}",
                (addr >> 24) & 0xFF, (addr >> 16) & 0xFF,
                (addr >> 8)  & 0xFF,  addr         & 0xFF));
        return result;
    }

    // ── Layout helpers ────────────────────────────────────────────────────────

    private void RelayoutDynamic()
    {
        int w = ClientSize.Width - 40;
        int h = ClientSize.Height;
        if (w < 100) return;
        if (resultsGrid        != null) { resultsGrid.Width = w; resultsGrid.Height = h - 290; }
        if (progressBar        != null)   progressBar.Width  = w - 130;
        if (statusLabel        != null)   statusLabel.Width  = w;
        if (selectAllBtn       != null)   selectAllBtn.Top        = resultsGrid.Bottom + 8;
        if (selectRespondedBtn != null)   selectRespondedBtn.Top  = resultsGrid.Bottom + 8;
        if (clearSelBtn        != null)   clearSelBtn.Top         = resultsGrid.Bottom + 8;
        if (addBtn             != null)   addBtn.Top              = resultsGrid.Bottom + 46;
    }

    private Label MakeLabel(string text, int x, int y)
    {
        return new Label {
            Text = text, ForeColor = FgText, BackColor = Color.Transparent,
            AutoSize = true, Location = new Point(x, y + 4)
        };
    }

    private Button MakeButton(string text, int x, int y, Color bg, Color fg, int width = 72)
    {
        var b = new Button {
            Text = text, Location = new Point(x, y), Size = new Size(width, 30),
            BackColor = bg, ForeColor = fg, FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold), Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    private DataGridViewCellStyle MakeCellStyle(Color back, Color fore, Color selBack)
    {
        return new DataGridViewCellStyle {
            BackColor = back, ForeColor = fore,
            SelectionBackColor = selBack, SelectionForeColor = fore,
            Alignment = DataGridViewContentAlignment.MiddleCenter
        };
    }
}
