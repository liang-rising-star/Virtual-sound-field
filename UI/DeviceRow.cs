using VirtualSoundField.Audio;

namespace VirtualSoundField.UI;

/// <summary>One row in the device list: name, live status, volume slider, delay trim, remove button.</summary>
public sealed class DeviceRow : Panel
{
    private readonly Label nameLabel;
    private readonly Label stateLabel;
    private readonly Label percentLabel;
    private readonly Label msLabel;
    private readonly TrackBar volumeBar;
    private readonly NumericUpDown delayBox;
    private readonly Button removeButton;
    private Label? channelLabel;
    private TrackBar? hwVolumeBar;
    private Label? hwPercentLabel;
    private Label? hwLabel;
    private System.Windows.Forms.Timer? hwDebounce;

    public string DeviceId { get; }
    public string DeviceName { get; }
    public int VolumePercent => volumeBar.Value;
    public int DelayMs => (int)delayBox.Value;
    public int HwVolumePercent => hwVolumeBar?.Value ?? 100;

    /// <summary>Slider value with a squared taper, which feels roughly linear in loudness.</summary>
    public float VolumeScalar
    {
        get
        {
            float v = volumeBar.Value / 100f;
            return v * v;
        }
    }

    public event Action<DeviceRow>? RemoveClicked;
    public event Action<DeviceRow>? VolumeChanged;
    public event Action<DeviceRow>? DelayChanged;
    public event Action<DeviceRow>? HwVolumeChanged;

    public DeviceRow(string deviceId, string deviceName, int volumePercent, int delayMs)
    {
        DeviceId = deviceId;
        DeviceName = deviceName;

        Margin = new Padding(2, 3, 2, 3);
        BackColor = SystemColors.Window;
        BorderStyle = BorderStyle.FixedSingle;

        nameLabel = new Label
        {
            Text = deviceName,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            AutoSize = false,
            AutoEllipsis = true,
        };
        stateLabel = new Label
        {
            Text = "",
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.MiddleRight,
            AutoSize = false,
            AutoEllipsis = true,
        };
        percentLabel = new Label
        {
            Text = volumePercent + "%",
            TextAlign = ContentAlignment.MiddleRight,
            AutoSize = false,
        };
        volumeBar = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(volumePercent, 0, 100),
            TickStyle = TickStyle.None,
            AutoSize = false,
            SmallChange = 2,
            LargeChange = 10,
        };
        volumeBar.ValueChanged += (_, _) =>
        {
            percentLabel.Text = volumeBar.Value + "%";
            VolumeChanged?.Invoke(this);
        };
        delayBox = new NumericUpDown
        {
            Minimum = 0,
            Maximum = OutputPipeline.MaxExtraDelayMs,
            Increment = 10,
            Value = Math.Clamp(delayMs, 0, OutputPipeline.MaxExtraDelayMs),
            TextAlign = HorizontalAlignment.Right,
        };
        delayBox.ValueChanged += (_, _) => DelayChanged?.Invoke(this);
        msLabel = new Label
        {
            Text = "ms",
            AutoSize = true, // sized from the actual (DPI-scaled) font so it never truncates
            ForeColor = SystemColors.GrayText,
        };
        removeButton = new Button
        {
            Text = "✕",
            FlatStyle = FlatStyle.Flat,
            TabStop = false,
        };
        removeButton.FlatAppearance.BorderSize = 0;
        removeButton.Click += (_, _) => RemoveClicked?.Invoke(this);

        var tip = new ToolTip();
        tip.SetToolTip(nameLabel, deviceName);
        tip.SetToolTip(removeButton, "移除此设备");
        string delayHint = "此设备的额外延迟。在快速设备（有线/2.4 GHz）上调高以与较慢的蓝牙耳机对齐。";
        tip.SetToolTip(delayBox, delayHint);
        tip.SetToolTip(msLabel, delayHint);

        Controls.AddRange(new Control[] { nameLabel, stateLabel, volumeBar, percentLabel, delayBox, msLabel, removeButton });
        Resize += (_, _) => Relayout();
        Relayout();
    }

    /// <summary>Add a volume slider for this device.</summary>
    public void AddHardwareVolumeSlider(int initialPercent)
    {
        hwLabel = new Label
        {
            Text = "\U0001F509",
            AutoSize = true,
            Font = new Font("Segoe UI Emoji", 10f),
        };
        hwPercentLabel = new Label
        {
            Text = initialPercent + "%",
            TextAlign = ContentAlignment.MiddleRight,
            AutoSize = false,
        };
        hwVolumeBar = new TrackBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(initialPercent, 0, 100),
            TickStyle = TickStyle.None,
            AutoSize = false,
            SmallChange = 2,
            LargeChange = 10,
        };
        hwDebounce = new System.Windows.Forms.Timer { Interval = 300 };
        hwDebounce.Tick += (_, _) =>
        {
            hwDebounce.Stop();
            HwVolumeChanged?.Invoke(this);
        };
        hwVolumeBar.MouseUp += (_, _) =>
        {
            hwDebounce?.Stop();
            HwVolumeChanged?.Invoke(this);
        };
        hwVolumeBar.ValueChanged += (_, _) =>
        {
            hwPercentLabel!.Text = hwVolumeBar.Value + "%";
        };

        var tip = new ToolTip();
        tip.SetToolTip(hwLabel, "设备音量");
        tip.SetToolTip(hwVolumeBar, "设备音量");

        Controls.AddRange(new Control[] { hwVolumeBar, hwPercentLabel, hwLabel });
        Relayout();
    }

    public void SetVolumePercent(int percent)
    {
        int val = Math.Clamp(percent, 0, 100);
        volumeBar.Value = val;
        percentLabel.Text = val + "%";
    }

    public void SetHwVolumePercent(int percent)
    {
        if (hwVolumeBar != null)
        {
            int val = Math.Clamp(percent, 0, 100);
            hwVolumeBar.Value = val;
            hwPercentLabel!.Text = val + "%";
        }
    }

    public void SetState(string text, bool error)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetState(text, error));
            return;
        }
        stateLabel.Text = text;
        stateLabel.ForeColor = error ? Color.Firebrick : SystemColors.GrayText;
    }

    /// <summary>设置通道标签（模式分流时显示 "前左 (FL)" 等）。</summary>
    public void SetChannelName(string channelName)
    {
        if (channelLabel == null)
        {
            channelLabel = new Label
            {
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 120, 215),
                AutoSize = true,
                AutoEllipsis = true,
            };
            Controls.Add(channelLabel);
        }
        channelLabel.Text = channelName;
        Relayout();
    }

    /// <summary>清除通道标签。</summary>
    public void ClearChannelName()
    {
        if (channelLabel != null)
        {
            Controls.Remove(channelLabel);
            channelLabel.Dispose();
            channelLabel = null;
            Relayout();
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        // fires when the row's monitor DPI changes (window moved to another screen)
        base.OnDpiChangedAfterParent(e);
        Relayout();
    }

    /// <summary>
    /// All sizes derive from a single DPI factor (DeviceDpi / 96) so rows look right at
    /// any Windows display scaling, including rows added after the window was shown
    /// (WinForms does not rescale controls added at runtime). At 100 % the factor is
    /// 1.0, so positions are pixel-identical to the original hand-tuned layout.
    /// </summary>
    private void Relayout()
    {
        float k = DeviceDpi / 96f;
        int S(double v) => (int)Math.Round(v * k);

        var margin = new Padding(S(2), S(3), S(2), S(3));
        if (Margin != margin)
            Margin = margin;

        bool hasHw = hwVolumeBar != null;
        bool hasChannel = channelLabel != null;
        int baseHeight = hasHw ? S(88) : S(58);
        int desiredHeight = hasChannel ? baseHeight + S(16) : baseHeight;
        if (Height != desiredHeight)
            Height = desiredHeight;

        int w = ClientSize.Width;
        int labelH = S(18);

        int nameY = S(6);
        if (hasChannel && channelLabel != null)
        {
            channelLabel.Location = new Point(S(8), S(2));
            nameY = S(18);
        }

        nameLabel.Height = labelH;
        stateLabel.Height = labelH;
        stateLabel.Width = S(170);
        nameLabel.Location = new Point(S(8), nameY);
        stateLabel.Location = new Point(w - stateLabel.Width - S(8), nameY);
        nameLabel.Width = Math.Max(S(40), w - stateLabel.Width - S(24));

        int rowY = nameY + S(21);
        removeButton.Size = new Size(S(26), S(26));
        removeButton.Location = new Point(w - removeButton.Width - S(6), rowY);
        msLabel.Location = new Point(removeButton.Left - msLabel.Width - S(2), rowY + S(6));
        delayBox.Width = S(54);
        delayBox.Location = new Point(msLabel.Left - delayBox.Width - S(2), rowY + S(2));
        percentLabel.Width = S(42);
        percentLabel.Height = labelH;
        percentLabel.Location = new Point(delayBox.Left - percentLabel.Width - S(6), rowY + S(5));
        volumeBar.Height = S(26);
        volumeBar.Location = new Point(S(4), rowY + S(1));
        volumeBar.Width = Math.Max(S(60), percentLabel.Left - volumeBar.Left - S(4));

        // Hardware volume row: slider | percent | label
        if (hasHw && hwVolumeBar != null && hwPercentLabel != null && hwLabel != null)
        {
            int hwRowY = S(57);
            hwPercentLabel.Width = S(42);
            hwPercentLabel.Height = labelH;
            hwPercentLabel.Location = new Point(delayBox.Left - hwPercentLabel.Width - S(6), hwRowY + S(5));
            hwLabel.Location = new Point(hwPercentLabel.Left - hwLabel.Width - S(4), hwRowY + S(5));
            hwVolumeBar.Height = S(26);
            hwVolumeBar.Location = new Point(S(4), hwRowY + S(1));
            hwVolumeBar.Width = Math.Max(S(60), hwLabel.Left - hwVolumeBar.Left - S(4));
        }
    }
}
