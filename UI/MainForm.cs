using VirtualSoundField.Audio;
using VirtualSoundField.Settings;

namespace VirtualSoundField.UI;

public sealed class MainForm : Form
{
    private readonly MirrorEngine engine = new();
    private readonly AppSettings settings;

    // === 布局控件 ===
    private readonly Panel topPanel;
    private readonly Panel modePanel;
    private readonly Panel volumePanel;
    private readonly Panel bottomPanel;
    private readonly Label mirrorFromLabel;
    private readonly ComboBox captureCombo;
    private readonly Label statusLabel;
    private readonly FlowLayoutPanel deviceList;
    private readonly Button startButton;
    private readonly Label bufferLabel;
    private readonly NumericUpDown bufferBox;
    private readonly Label bufferMsLabel;
    private readonly Label skipLabel;
    private readonly NumericUpDown skipBox;
    private readonly Label skipMsLabel;

    // === 模式选择 ===
    private readonly RadioButton radLR;
    private readonly RadioButton radFrontRear;
    private readonly RadioButton radQuad;
    private readonly RadioButton rad51;

    // === 系统音量 ===
    private readonly Panel masterPanel;
    private readonly Button volUpButton;
    private readonly Button volDownButton;
    private readonly Label masterVolLabel;
    private readonly TrackBar sysVolBar;
    private readonly Label sysVolPercentLabel;
    private readonly Button muteButton;

    // === 托盘 ===
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem trayToggle;
    private readonly Icon appIcon = LoadAppIcon();
    private readonly Icon idleIcon = TrayIconFactory.Create(active: false);
    private readonly Icon activeIcon = TrayIconFactory.Create(active: true);

    private readonly System.Windows.Forms.Timer uiTimer;
    private bool suppressCaptureChange;
    private bool suppressModeChange;
    private bool settingsDirty;
    private bool trayHintShown;
    private bool exiting;
    private bool suppressSysVolChange;
    private DateTime sysVolSuppressUntil = DateTime.MinValue;

    private IEnumerable<DeviceRow> Rows => deviceList.Controls.OfType<DeviceRow>();

    private int Scale(double v) => (int)Math.Round(v * DeviceDpi / 96.0);

    public MainForm()
    {
        settings = AppSettings.Load();

        Text = "Virtual sound field";
        Icon = appIcon;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(520, 300);

        // === 总音量 +/- ===
        masterPanel = new Panel { Dock = DockStyle.Top, Height = 32 };
        masterVolLabel = new Label { Text = "总音量：", AutoSize = true };
        volDownButton = new Button { Text = "-", FlatStyle = FlatStyle.Flat, Width = 32, Height = 26 };
        volDownButton.FlatAppearance.BorderSize = 0;
        volDownButton.Click += (_, _) => AdjustAllVolumes(-2);
        volUpButton = new Button { Text = "+", FlatStyle = FlatStyle.Flat, Width = 32, Height = 26 };
        volUpButton.FlatAppearance.BorderSize = 0;
        volUpButton.Click += (_, _) => AdjustAllVolumes(2);
        masterPanel.Controls.AddRange(new Control[] { masterVolLabel, volDownButton, volUpButton });

        // === 模式选择 ===
        modePanel = new Panel { Dock = DockStyle.Top, Height = 36 };
        var lblMode = new Label { Text = "模式：", AutoSize = true };
        radLR = new RadioButton { Text = "左右分离", AutoSize = true, Checked = true };
        radFrontRear = new RadioButton { Text = "前后立体声", AutoSize = true };
        radQuad = new RadioButton { Text = "四角环绕", AutoSize = true };
        rad51 = new RadioButton { Text = "5.1声道", AutoSize = true };
        radLR.CheckedChanged += (_, _) => OnModeChanged();
        radFrontRear.CheckedChanged += (_, _) => OnModeChanged();
        radQuad.CheckedChanged += (_, _) => OnModeChanged();
        rad51.CheckedChanged += (_, _) => OnModeChanged();
        modePanel.Controls.AddRange(new Control[] { lblMode, radLR, radFrontRear, radQuad, rad51 });

        // === 捕获源 + 状态 ===
        topPanel = new Panel { Dock = DockStyle.Top };
        mirrorFromLabel = new Label { Text = "镜像来源：", AutoSize = true };
        captureCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        captureCombo.DropDown += (_, _) => PopulateCaptureCombo();
        captureCombo.SelectedIndexChanged += (_, _) => OnCaptureSelectionChanged();
        statusLabel = new Label
        {
            Text = "已停止 — 选择模式，分配设备，然后点击开始。",
            ForeColor = SystemColors.GrayText,
            AutoSize = false,
            AutoEllipsis = true,
        };
        topPanel.Controls.AddRange(new Control[] { mirrorFromLabel, captureCombo, statusLabel });

        // === 系统音量 ===
        volumePanel = new Panel { Dock = DockStyle.Top, Height = 36 };
        var volLabel = new Label { Text = "系统音量：", AutoSize = true };
        sysVolPercentLabel = new Label { Text = "100%", TextAlign = ContentAlignment.MiddleRight, AutoSize = false };
        sysVolBar = new TrackBar
        {
            Minimum = 0, Maximum = 100, Value = 100,
            TickStyle = TickStyle.None, AutoSize = false,
            SmallChange = 2, LargeChange = 10,
        };
        sysVolBar.ValueChanged += (_, _) =>
        {
            sysVolPercentLabel.Text = sysVolBar.Value + "%";
            if (!suppressSysVolChange) engine.SetSystemVolume(sysVolBar.Value / 100f);
        };
        muteButton = new Button { Text = "\U0001F50A", FlatStyle = FlatStyle.Flat };
        muteButton.FlatAppearance.BorderSize = 0;
        muteButton.Click += (_, _) =>
        {
            bool muted = muteButton.Text == "\U0001F507";
            muteButton.Text = muted ? "\U0001F50A" : "\U0001F507";
            engine.SetSystemMute(!muted);
        };
        sysVolBar.Value = (int)(engine.GetSystemVolume() * 100);
        sysVolPercentLabel.Text = sysVolBar.Value + "%";
        volumePanel.Controls.AddRange(new Control[] { volLabel, sysVolBar, sysVolPercentLabel, muteButton });

        // === 通道列表（中央区域） ===
        deviceList = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
        };
        deviceList.ClientSizeChanged += (_, _) => ResizeRows();

        // === 底部：添加设备 + 缓冲 + 丢弃 + 开始 ===
        bottomPanel = new Panel { Dock = DockStyle.Bottom };
        var addBtn = new Button { Text = "添加设备  \u25BE" };
        addBtn.Click += (_, _) => ShowAddDeviceMenu();
        bufferLabel = new Label { Text = "缓冲：", AutoSize = true };
        bufferBox = new NumericUpDown
        {
            Minimum = OutputPipeline.MinBufferMs,
            Maximum = OutputPipeline.MaxBufferMs,
            Increment = 10,
            Value = Math.Clamp(settings.BufferMs, OutputPipeline.MinBufferMs, OutputPipeline.MaxBufferMs),
            TextAlign = HorizontalAlignment.Right,
        };
        bufferBox.ValueChanged += (_, _) => OnBufferChanged();
        bufferMsLabel = new Label { Text = "ms", AutoSize = true, ForeColor = SystemColors.GrayText };
        var bufferTip = new ToolTip();
        bufferTip.SetToolTip(bufferBox, "每个设备播放前的音频队列。越低延迟越好，爆音则调高。");
        skipLabel = new Label { Text = "丢弃：", AutoSize = true };
        skipBox = new NumericUpDown
        {
            Minimum = 10, Maximum = 500, Increment = 10,
            Value = Math.Clamp(settings.SkipThresholdMs, 10, 500),
            TextAlign = HorizontalAlignment.Right,
        };
        skipBox.ValueChanged += (_, _) => OnSkipChanged();
        skipMsLabel = new Label { Text = "ms", AutoSize = true, ForeColor = SystemColors.GrayText };
        var skipTip = new ToolTip();
        skipTip.SetToolTip(skipBox, "缓冲超出目标这么多时丢弃音频。越低同步越好，断音则调高。");
        startButton = new Button { Text = "开始" };
        startButton.Click += (_, _) => ToggleMirroring();
        bottomPanel.Controls.AddRange(new Control[] { addBtn, bufferLabel, bufferBox, bufferMsLabel, skipLabel, skipBox, skipMsLabel, startButton });

        // Dock 顺序很重要
        Controls.Add(deviceList);
        Controls.Add(masterPanel);
        Controls.Add(volumePanel);
        Controls.Add(modePanel);
        Controls.Add(topPanel);
        Controls.Add(bottomPanel);

        // === 托盘 ===
        trayToggle = new ToolStripMenuItem("开始镜像", null, (_, _) => ToggleMirroring());
        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add(new ToolStripMenuItem("打开 Virtual sound field", null, (_, _) => RestoreFromTray()));
        trayMenu.Items.Add(trayToggle);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));
        tray = new NotifyIcon { Icon = idleIcon, Text = "Virtual sound field — 已停止", Visible = true, ContextMenuStrip = trayMenu };
        tray.DoubleClick += (_, _) => RestoreFromTray();

        // === 引擎事件 ===
        engine.ChannelFailed += (ch, _) => SafeInvoke(() =>
        {
            Rows.FirstOrDefault(r => r.Tag is int idx && idx == ch)?.SetState("设备丢失", error: true);
            tray.ShowBalloonTip(3000, "Virtual sound field", $"通道 {ch} 输出丢失，其他设备继续播放。", ToolTipIcon.Warning);
        });
        engine.CaptureStopped += _ => SafeInvoke(() =>
        {
            engine.Stop();
            SetUiStopped();
            tray.ShowBalloonTip(4000, "Virtual sound field", "镜像已停止 — 捕获设备已更改或消失。", ToolTipIcon.Warning);
        });

        // === 恢复上次状态 ===
        RestoreSettings();
        PopulateCaptureCombo();

        uiTimer = new System.Windows.Forms.Timer { Interval = 500 };
        uiTimer.Tick += (_, _) => OnUiTimerTick();
        uiTimer.Start();
    }

    // ---------------------------------------------------------------- DPI layout

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); LayoutForm(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); LayoutForm(); }

    private void LayoutForm()
    {
        SuspendLayout();
        topPanel.SuspendLayout();
        bottomPanel.SuspendLayout();

        int pad = Scale(10);
        int formW = Scale(560);
        int topH = Scale(64);
        int botH = Scale(48);

        topPanel.Height = topH;
        bottomPanel.Height = botH;
        deviceList.Padding = new Padding(Scale(6), Scale(2), Scale(6), Scale(2));

        ClientSize = new Size(formW, ClientSize.Height);

        // top panel
        mirrorFromLabel.Location = new Point(pad, Scale(12));
        int comboLeft = Math.Max(Scale(86), mirrorFromLabel.Right + Scale(4));
        captureCombo.Location = new Point(comboLeft, Scale(8));
        captureCombo.Width = Math.Max(Scale(80), formW - comboLeft - pad);
        statusLabel.Location = new Point(pad, Scale(40));
        statusLabel.Size = new Size(formW - Scale(20), Scale(18));

        // mode panel
        modePanel.Height = Scale(32);
        int modeY = (modePanel.Height - Scale(18)) / 2;
        int modeX = pad;
        foreach (Control c in modePanel.Controls)
        {
            c.Location = new Point(modeX, modeY);
            modeX = c.Right + Scale(12);
        }

        // master panel
        int masterH = Scale(28);
        masterPanel.Height = masterH;
        masterVolLabel.Location = new Point(pad, Scale(4));
        volDownButton.Size = new Size(Scale(32), Scale(24));
        volDownButton.Location = new Point(masterVolLabel.Right + Scale(8), (masterH - volDownButton.Height) / 2);
        volUpButton.Size = new Size(Scale(32), Scale(24));
        volUpButton.Location = new Point(volDownButton.Right + Scale(4), (masterH - volUpButton.Height) / 2);

        // volume panel
        int volPanelH = Scale(32);
        volumePanel.Height = volPanelH;
        var volLbl = volumePanel.Controls[0];
        volLbl.Location = new Point(pad, Scale(6));
        muteButton.Size = new Size(Scale(28), Scale(24));
        muteButton.Location = new Point(formW - muteButton.Width - pad, (volPanelH - muteButton.Height) / 2);
        sysVolPercentLabel.Width = Scale(40);
        sysVolPercentLabel.Height = Scale(18);
        sysVolPercentLabel.Location = new Point(muteButton.Left - sysVolPercentLabel.Width - Scale(2), (volPanelH - sysVolPercentLabel.Height) / 2);
        sysVolBar.Height = Scale(24);
        sysVolBar.Location = new Point(volLbl.Right + Scale(4), (volPanelH - sysVolBar.Height) / 2);
        sysVolBar.Width = Math.Max(Scale(80), sysVolPercentLabel.Left - sysVolBar.Left - Scale(4));

        // bottom panel
        var addBtn = bottomPanel.Controls[0];
        addBtn.Size = new Size(Scale(120), Scale(30));
        addBtn.Location = new Point(pad, (botH - addBtn.Height) / 2);
        startButton.Size = new Size(Scale(120), Scale(30));
        startButton.Location = new Point(formW - startButton.Width - pad, (botH - startButton.Height) / 2);
        bufferBox.Size = new Size(Scale(56), Scale(24));
        bufferLabel.Location = new Point(addBtn.Right + Scale(12), (botH - bufferLabel.Height) / 2);
        bufferBox.Location = new Point(bufferLabel.Right + Scale(4), (botH - bufferBox.Height) / 2);
        bufferMsLabel.Location = new Point(bufferBox.Right + Scale(2), (botH - bufferMsLabel.Height) / 2);
        skipBox.Size = new Size(Scale(56), Scale(24));
        skipLabel.Location = new Point(bufferMsLabel.Right + Scale(12), (botH - skipLabel.Height) / 2);
        skipBox.Location = new Point(skipLabel.Right + Scale(4), (botH - skipBox.Height) / 2);
        skipMsLabel.Location = new Point(skipBox.Right + Scale(2), (botH - skipMsLabel.Height) / 2);

        bottomPanel.ResumeLayout();
        topPanel.ResumeLayout();
        ResumeLayout();

        ResizeRows();
        UpdateWindowHeight();
    }

    // ---------------------------------------------------------------- mode

    private RoutingMode GetSelectedMode()
    {
        if (radLR.Checked) return RoutingMode.LRSplit;
        if (radFrontRear.Checked) return RoutingMode.FrontRear;
        if (radQuad.Checked) return RoutingMode.QuadSurround;
        return RoutingMode.FivePointOne;
    }

    private void OnModeChanged()
    {
        if (suppressModeChange || engine.IsRunning) return;
        RebuildChannelRows();
    }

    /// <summary>根据当前模式重建通道行。保留已有的设备绑定。</summary>
    private void RebuildChannelRows()
    {
        var mode = GetSelectedMode();
        int chCount = ModeProcessor.GetChannelCount(mode);

        // 保存现有绑定：通道索引 → 设备ID
        var savedBindings = new Dictionary<int, string>();
        foreach (var row in Rows)
        {
            if (row.Tag is int idx && !string.IsNullOrEmpty(row.DeviceId))
                savedBindings[idx] = row.DeviceId;
        }

        deviceList.Controls.Clear();
        for (int i = 0; i < chCount; i++)
        {
            string channelName = ModeProcessor.GetChannelName(mode, i);
            string deviceId = savedBindings.TryGetValue(i, out var id) ? id : "";
            string deviceName = deviceId != "" ? GetDeviceName(deviceId) : "（未分配）";

            var row = new DeviceRow(deviceId, deviceName, 100, 0);
            row.Tag = i; // 通道索引
            row.SetChannelName(channelName);
            row.RemoveClicked += OnRowRemoveClicked;
            row.VolumeChanged += OnRowVolumeChanged;
            row.DelayChanged += OnRowDelayChanged;
            row.HwVolumeChanged += OnRowHwVolumeChanged;
            row.AddHardwareVolumeSlider(100);
            deviceList.Controls.Add(row);
        }
        ResizeRows();
        UpdateWindowHeight();
    }

    private string GetDeviceName(string deviceId)
    {
        var devices = MirrorEngine.ListRenderDevices();
        return devices.FirstOrDefault(d => d.Id == deviceId).Name ?? deviceId;
    }

    // ---------------------------------------------------------------- capture source

    private sealed class CaptureChoice
    {
        public string? Id { get; init; }
        public string Text { get; init; } = "";
        public override string ToString() => Text;
    }

    private void PopulateCaptureCombo()
    {
        suppressCaptureChange = true;
        try
        {
            var devices = MirrorEngine.ListRenderDevices();
            string? defaultId = MirrorEngine.GetDefaultRenderId();
            string defaultName = devices.FirstOrDefault(d => d.Id == defaultId).Name ?? "无设备";

            captureCombo.BeginUpdate();
            captureCombo.Items.Clear();
            captureCombo.Items.Add(new CaptureChoice { Id = null, Text = $"系统默认  ({defaultName})" });
            int selectIndex = 0;
            foreach (var (id, name) in devices)
            {
                captureCombo.Items.Add(new CaptureChoice { Id = id, Text = name });
                if (settings.CaptureDeviceId == id)
                    selectIndex = captureCombo.Items.Count - 1;
            }
            captureCombo.SelectedIndex = selectIndex;
            captureCombo.EndUpdate();
        }
        finally { suppressCaptureChange = false; }
    }

    private void OnCaptureSelectionChanged()
    {
        if (suppressCaptureChange) return;
        settings.CaptureDeviceId = (captureCombo.SelectedItem as CaptureChoice)?.Id;
        SaveSettings();
    }

    // ---------------------------------------------------------------- mirroring

    private void ToggleMirroring()
    {
        if (engine.IsRunning)
        {
            engine.Stop();
            SetUiStopped();
        }
        else StartMirroring();
    }

    private void StartMirroring()
    {
        var rows = Rows.ToList();
        if (rows.All(r => string.IsNullOrEmpty(r.DeviceId)))
        {
            RestoreFromTray();
            MessageBox.Show(this, "请先为至少一个通道分配输出设备。",
                "Virtual sound field", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var mode = GetSelectedMode();
        var statuses = engine.Start(settings.CaptureDeviceId, settings.BufferMs, mode);

        // 绑定各通道设备
        foreach (var row in rows)
        {
            if (row.Tag is int chIdx && !string.IsNullOrEmpty(row.DeviceId))
            {
                var req = new OutputRequest(row.DeviceId, row.VolumeScalar, row.DelayMs);
                var status = engine.AssignChannel(chIdx, req);
                row.SetState(status.Detail, !status.Ok);
            }
        }

        if (engine.IsRunning)
        {
            var format = engine.CaptureFormat!;
            statusLabel.Text = $"镜像中：{engine.CaptureDeviceName}  ({format.SampleRate / 1000.0:0.#} kHz, {format.Channels}ch) → [{mode}]";
            statusLabel.ForeColor = Color.FromArgb(0, 130, 60);
            startButton.Text = "停止";
        }
        else
        {
            statusLabel.Text = "无法启动 — 没有可用的输出设备。";
            statusLabel.ForeColor = Color.Firebrick;
        }
        UpdateTray();
    }

    private void SetUiStopped()
    {
        startButton.Text = "开始";
        statusLabel.Text = "已停止 — 选择模式，分配设备，然后点击开始。";
        statusLabel.ForeColor = SystemColors.GrayText;
        foreach (var row in Rows) row.SetState("", error: false);
        UpdateTray();
    }

    private void UpdateTray()
    {
        bool running = engine.IsRunning;
        trayToggle.Text = running ? "停止镜像" : "开始镜像";
        tray.Icon = running ? activeIcon : idleIcon;
        tray.Text = running ? "Virtual sound field — 镜像中" : "Virtual sound field — 已停止";
    }

    private void OnUiTimerTick()
    {
        if (settingsDirty) { settingsDirty = false; SaveSettings(); }
        SyncSystemVolume();
        if (!engine.IsRunning) return;
        foreach (var row in Rows)
        {
            if (row.Tag is int chIdx && engine.TryGetChannelInfo(chIdx, out var pipeline))
                row.SetState($"{pipeline.DeviceSampleRate / 1000.0:0.#} kHz • 缓冲 {pipeline.BufferedMs:0} ms", error: false);
        }
    }

    private void SyncSystemVolume()
    {
        if (DateTime.Now < sysVolSuppressUntil) return;
        float vol = engine.GetSystemVolume();
        int pct = (int)(vol * 100);
        if (pct != sysVolBar.Value)
        {
            suppressSysVolChange = true;
            sysVolBar.Value = pct;
            sysVolPercentLabel.Text = pct + "%";
            suppressSysVolChange = false;
        }
    }

    // ---------------------------------------------------------------- device rows

    private void ShowAddDeviceMenu()
    {
        var menu = new ContextMenuStrip();
        var mode = GetSelectedMode();
        int chCount = ModeProcessor.GetChannelCount(mode);

        // 找到尚未分配的通道
        var unassignedChannels = new List<int>();
        for (int i = 0; i < chCount; i++)
        {
            bool assigned = Rows.Any(r => r.Tag is int idx && idx == i && !string.IsNullOrEmpty(r.DeviceId));
            if (!assigned) unassignedChannels.Add(i);
        }

        if (unassignedChannels.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("所有通道已分配") { Enabled = false });
            menu.Show(bottomPanel.Controls[0], new Point(0, -menu.Height));
            return;
        }

        string? captureId = engine.IsRunning ? engine.CaptureDeviceId
            : settings.CaptureDeviceId ?? MirrorEngine.GetDefaultRenderId();

        foreach (var (id, name) in MirrorEngine.ListRenderDevices())
        {
            bool isCapture = id == captureId;
            var item = new ToolStripMenuItem(isCapture ? name + "  — 捕获源" : name);
            string deviceId = id, deviceName = name;
            item.Click += (_, _) => AssignDeviceToNextChannel(deviceId, deviceName);
            menu.Items.Add(item);
        }

        menu.Show(bottomPanel.Controls[0], new Point(0, -menu.Height));
    }

    /// <summary>将设备分配到下一个未分配的通道。</summary>
    private void AssignDeviceToNextChannel(string deviceId, string deviceName)
    {
        var mode = GetSelectedMode();
        int chCount = ModeProcessor.GetChannelCount(mode);

        for (int i = 0; i < chCount; i++)
        {
            var row = Rows.FirstOrDefault(r => r.Tag is int idx && idx == i);
            if (row != null && string.IsNullOrEmpty(row.DeviceId))
            {
                // 更新行显示
                deviceList.Controls.Remove(row);
                row.RemoveClicked -= OnRowRemoveClicked;
                row.VolumeChanged -= OnRowVolumeChanged;
                row.DelayChanged -= OnRowDelayChanged;
                row.HwVolumeChanged -= OnRowHwVolumeChanged;
                row.Dispose();

                var newRow = new DeviceRow(deviceId, deviceName, 100, 0);
                newRow.Tag = i;
                newRow.SetChannelName(ModeProcessor.GetChannelName(mode, i));
                newRow.RemoveClicked += OnRowRemoveClicked;
                newRow.VolumeChanged += OnRowVolumeChanged;
                newRow.DelayChanged += OnRowDelayChanged;
                newRow.HwVolumeChanged += OnRowHwVolumeChanged;
                newRow.AddHardwareVolumeSlider(100);
                deviceList.Controls.Add(newRow);
                ResizeRows();
                UpdateWindowHeight();

                // 如果正在运行，立即绑定
                if (engine.IsRunning)
                {
                    var req = new OutputRequest(deviceId, newRow.VolumeScalar, newRow.DelayMs);
                    var status = engine.AssignChannel(i, req);
                    newRow.SetState(status.Detail, !status.Ok);
                }
                SaveSettings();
                return;
            }
        }
    }

    private void OnRowRemoveClicked(DeviceRow row)
    {
        if (row.Tag is int chIdx)
        {
            engine.UnassignChannel(chIdx);
            // 重置为空行而不是移除
            int idx = chIdx;
            string channelName = ModeProcessor.GetChannelName(engine.IsRunning ? engine.Mode : GetSelectedMode(), idx);
            deviceList.Controls.Remove(row);
            row.RemoveClicked -= OnRowRemoveClicked;
            row.VolumeChanged -= OnRowVolumeChanged;
            row.DelayChanged -= OnRowDelayChanged;
            row.HwVolumeChanged -= OnRowHwVolumeChanged;
            row.Dispose();

            var newRow = new DeviceRow("", "（未分配）", 100, 0);
            newRow.Tag = idx;
            newRow.SetChannelName(channelName);
            newRow.RemoveClicked += OnRowRemoveClicked;
            newRow.VolumeChanged += OnRowVolumeChanged;
            newRow.DelayChanged += OnRowDelayChanged;
            newRow.HwVolumeChanged += OnRowHwVolumeChanged;
            newRow.AddHardwareVolumeSlider(100);
            deviceList.Controls.Add(newRow);
            ResizeRows();
            UpdateWindowHeight();
        }
        SaveSettings();
    }

    private void OnRowVolumeChanged(DeviceRow row)
    {
        if (engine.IsRunning && row.Tag is int chIdx)
            engine.SetVolume(chIdx, row.VolumeScalar);
        settingsDirty = true;
    }

    private void OnRowHwVolumeChanged(DeviceRow row)
    {
        if (engine.IsRunning && row.Tag is int chIdx)
            engine.SetVolume(chIdx, row.HwVolumePercent / 100f);
        settingsDirty = true;
    }

    private void OnRowDelayChanged(DeviceRow row)
    {
        if (engine.IsRunning && row.Tag is int chIdx)
            engine.SetDelay(chIdx, row.DelayMs);
        settingsDirty = true;
    }

    private void OnBufferChanged()
    {
        settings.BufferMs = (int)bufferBox.Value;
        engine.SetBufferTarget(settings.BufferMs);
        settingsDirty = true;
    }

    private void OnSkipChanged()
    {
        settings.SkipThresholdMs = (int)skipBox.Value;
        engine.SetSkipThreshold(settings.SkipThresholdMs);
        settingsDirty = true;
    }

    private void AdjustAllVolumes(int delta)
    {
        sysVolSuppressUntil = DateTime.Now.AddSeconds(1);
        int sysVol = Math.Clamp((int)(engine.GetSystemVolume() * 100) + delta, 0, 100);
        suppressSysVolChange = true;
        engine.SetSystemVolume(sysVol / 100f);
        sysVolBar.Value = sysVol;
        sysVolPercentLabel.Text = sysVol + "%";
        suppressSysVolChange = false;

        foreach (var row in Rows)
        {
            if (row.HwVolumePercent >= 0)
            {
                int newHw = Math.Clamp(row.HwVolumePercent + delta, 0, 100);
                row.SetHwVolumePercent(newHw);
                if (engine.IsRunning && row.Tag is int chIdx)
                    engine.SetVolume(chIdx, newHw / 100f);
            }
        }
        settingsDirty = true;
    }

    private void ResizeRows()
    {
        int width = deviceList.ClientSize.Width - Scale(16);
        foreach (var row in Rows)
            row.Width = Math.Max(Scale(200), width);
    }

    private void UpdateWindowHeight()
    {
        int listHeight = 0;
        foreach (var row in Rows)
            listHeight += row.Height + row.Margin.Vertical;
        listHeight += deviceList.Padding.Vertical + Scale(6);

        int desired = topPanel.Height + volumePanel.Height + masterPanel.Height
                    + modePanel.Height + listHeight + bottomPanel.Height;
        int max = Screen.FromControl(this).WorkingArea.Height * 3 / 4;
        ClientSize = new Size(ClientSize.Width, Math.Min(desired, max));
    }

    // ---------------------------------------------------------------- settings

    private void RestoreSettings()
    {
        suppressModeChange = true;
        var mode = settings.Mode;
        switch (mode)
        {
            case RoutingMode.LRSplit: radLR.Checked = true; break;
            case RoutingMode.FrontRear: radFrontRear.Checked = true; break;
            case RoutingMode.QuadSurround: radQuad.Checked = true; break;
            case RoutingMode.FivePointOne: rad51.Checked = true; break;
        }
        suppressModeChange = false;

        deviceList.Controls.Clear();
        var activeDevices = MirrorEngine.ListRenderDevices().Select(d => d.Id).ToHashSet();
        int chCount = ModeProcessor.GetChannelCount(mode);

        for (int i = 0; i < chCount; i++)
        {
            string channelName = ModeProcessor.GetChannelName(mode, i);
            var saved = settings.Devices.FirstOrDefault(d => d.ChannelIndex == i);
            string deviceId = saved?.Id ?? "";
            string deviceName = deviceId != "" ? GetDeviceName(deviceId) : "（未分配）";
            int vol = saved?.Volume ?? 100;
            int delay = saved?.DelayMs ?? 0;

            var row = new DeviceRow(deviceId, deviceName, vol, delay);
            row.Tag = i;
            row.SetChannelName(channelName);
            row.RemoveClicked += OnRowRemoveClicked;
            row.VolumeChanged += OnRowVolumeChanged;
            row.DelayChanged += OnRowDelayChanged;
            row.HwVolumeChanged += OnRowHwVolumeChanged;
            row.AddHardwareVolumeSlider(saved?.HardwareVolume >= 0 ? saved.HardwareVolume : 100);
            if (deviceId != "" && !activeDevices.Contains(deviceId))
                row.SetState("未连接", error: false);
            deviceList.Controls.Add(row);
        }

        if (Rows.Any()) UpdateWindowHeight();
    }

    private void SyncSettingsFromRows()
    {
        settings.Mode = GetSelectedMode();
        settings.Devices = Rows.Select(r => new SavedDevice
        {
            Id = r.DeviceId,
            Name = r.DeviceName,
            Volume = r.VolumePercent,
            DelayMs = r.DelayMs,
            HardwareVolume = r.HwVolumePercent,
            ChannelIndex = r.Tag is int idx ? idx : -1,
        }).ToList();
    }

    private void SaveSettings()
    {
        SyncSettingsFromRows();
        settings.Save();
    }

    // ---------------------------------------------------------------- tray / lifecycle

    private static Icon LoadAppIcon()
    {
        using var stream = typeof(MainForm).Assembly
            .GetManifestResourceStream("VirtualSoundField.UI.Assets.earshare.ico");
        return stream != null ? new Icon(stream) : TrayIconFactory.Create(active: false);
    }

    private void SafeInvoke(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch { }
    }

    private void HideToTray()
    {
        Hide();
        if (!trayHintShown)
        {
            trayHintShown = true;
            tray.ShowBalloonTip(2500, "Virtual sound field", "仍在后台运行。双击重新打开，右键停止或退出。", ToolTipIcon.Info);
        }
    }

    private void RestoreFromTray() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private void ExitApp() { exiting = true; Close(); }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) HideToTray();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SaveSettings();
        uiTimer.Stop();
        engine.Dispose();
        tray.Visible = false;
        tray.Dispose();
        base.OnFormClosed(e);
    }
}
