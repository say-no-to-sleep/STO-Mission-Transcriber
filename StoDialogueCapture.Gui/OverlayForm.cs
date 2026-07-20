using System.Drawing;

namespace StoDialogueCapture.Gui;

internal sealed class OverlayForm : Form
{
    private static readonly Color BackgroundColor = Color.FromArgb(18, 24, 27);
    private static readonly Color ForegroundColor = Color.FromArgb(214, 235, 230);
    private static readonly Color SubPanelColor = Color.FromArgb(26, 33, 37);
    private static readonly Color BorderColor = Color.FromArgb(46, 58, 63);
    private static readonly Color Accent = Color.FromArgb(0, 137, 139);
    private static readonly Color Success = Color.FromArgb(30, 130, 76);
    private static readonly Color BrightGreen = Color.FromArgb(46, 204, 113);
    private static readonly Color Warning = Color.FromArgb(191, 120, 20);
    private static readonly Color Muted = Color.FromArgb(94, 103, 112);

    private readonly GuiSettings _settings;
    private readonly System.Windows.Forms.Timer _elapsedTimer = new() { Interval = 1000 };
    private DateTime _startedAt;
    private bool _persistEnabled;
    private bool _dragging;
    private Point _dragOffset;

    private readonly Label _elapsedLabel = new()
    {
        Text = "00:00:00",
        AutoSize = true,
        ForeColor = ForegroundColor,
        TextAlign = ContentAlignment.MiddleRight,
    };
    private readonly Button _expandButton = new()
    {
        Text = "Open window",
        AutoSize = true,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(35, 44, 49),
        ForeColor = ForegroundColor,
    };
    private readonly Button _closeButton = new()
    {
        Text = "X",
        Width = 26,
        Height = 22,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(35, 44, 49),
        ForeColor = ForegroundColor,
    };
    private readonly Panel _beaconDot = new()
    {
        Width = 14,
        Height = 14,
        BackColor = Muted,
    };
    private readonly Label _beaconLabel = new()
    {
        Text = "Idle - not capturing",
        AutoSize = true,
        Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
        ForeColor = ForegroundColor,
    };
    private readonly Label _speakerLabel = new()
    {
        Text = string.Empty,
        AutoSize = true,
        Font = new Font("Segoe UI", 9F, FontStyle.Bold),
        ForeColor = ForegroundColor,
    };
    private readonly Label _excerptLabel = new()
    {
        Text = string.Empty,
        AutoSize = false,
        Height = 48,
        AutoEllipsis = true,
        ForeColor = ForegroundColor,
    };
    private readonly Label _windowsValueLabel;
    private readonly Label _choicesValueLabel;
    private readonly Label _npcValueLabel;
    private readonly Label _objectivesValueLabel;

    public event EventHandler? ExpandRequested;
    public event EventHandler? Hidden;

    public OverlayForm(GuiSettings settings)
    {
        _settings = settings;

        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        Width = 360;
        Height = 210;
        BackColor = BackgroundColor;
        ForeColor = ForegroundColor;
        Padding = new Padding(1);
        Font = new Font("Segoe UI", 9F);

        _windowsValueLabel = CreateCounterValueLabel();
        _choicesValueLabel = CreateCounterValueLabel();
        _npcValueLabel = CreateCounterValueLabel();
        _objectivesValueLabel = CreateCounterValueLabel();

        Controls.Add(BuildLayout());

        Paint += (_, e) =>
        {
            using var pen = new Pen(BorderColor);
            e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        };

        _expandButton.Click += (_, _) => ExpandRequested?.Invoke(this, EventArgs.Empty);
        _closeButton.Click += (_, _) =>
        {
            _settings.OverlayVisible = false;
            _settings.Save(GuiSettings.DefaultPath());
            Hide();
            Hidden?.Invoke(this, EventArgs.Empty);
        };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                _settings.OverlayVisible = false;
                _settings.Save(GuiSettings.DefaultPath());
                Hide();
                Hidden?.Invoke(this, EventArgs.Empty);
            }
        };

        _elapsedTimer.Tick += (_, _) =>
        {
            var elapsed = DateTime.Now - _startedAt;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }
            _elapsedLabel.Text = elapsed.ToString(@"hh\:mm\:ss");
        };

        AttachDragHandlers(this);

        PositionInitial();
        // Position is persisted at drag end (OnDragMouseUp) and on hide/close,
        // not per Move event: dragging fires Move continuously and would write
        // settings.json hundreds of times per drag.
        Move += (_, _) =>
        {
            if (!_persistEnabled)
            {
                return;
            }
            _settings.OverlayX = Location.X;
            _settings.OverlayY = Location.Y;
        };
        Shown += (_, _) => _persistEnabled = true;
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = BackgroundColor,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(8, 6, 8, 8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));

        root.Controls.Add(BuildTitleRow(), 0, 0);
        root.Controls.Add(BuildBeaconRow(), 0, 1);
        root.Controls.Add(BuildLastCapturedArea(), 0, 2);
        root.Controls.Add(BuildCounterRow(), 0, 3);
        return root;
    }

    private Control BuildTitleRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = BackgroundColor,
            ColumnCount = 4,
            RowCount = 1,
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var titleLabel = new Label
        {
            Text = "Capture overlay",
            AutoSize = true,
            ForeColor = Muted,
            Anchor = AnchorStyles.Left,
        };
        _elapsedLabel.Anchor = AnchorStyles.Right;
        _expandButton.Anchor = AnchorStyles.Right;
        _closeButton.Anchor = AnchorStyles.Right;

        row.Controls.Add(titleLabel, 0, 0);
        row.Controls.Add(_elapsedLabel, 1, 0);
        row.Controls.Add(_expandButton, 2, 0);
        row.Controls.Add(_closeButton, 3, 0);
        return row;
    }

    private Control BuildBeaconRow()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = SubPanelColor,
            ColumnCount = 2,
            RowCount = 1,
            Margin = new Padding(0, 4, 0, 4),
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _beaconDot.Margin = new Padding(8, 10, 8, 8);
        _beaconLabel.Margin = new Padding(0, 10, 0, 8);
        panel.Controls.Add(_beaconDot, 0, 0);
        panel.Controls.Add(_beaconLabel, 1, 0);
        return panel;
    }

    private Control BuildLastCapturedArea()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = BackgroundColor,
            ColumnCount = 1,
            RowCount = 2,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _speakerLabel.Margin = new Padding(0, 4, 0, 0);
        _excerptLabel.Dock = DockStyle.Fill;
        panel.Controls.Add(_speakerLabel, 0, 0);
        panel.Controls.Add(_excerptLabel, 0, 1);
        return panel;
    }

    private Control BuildCounterRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = BackgroundColor,
            ColumnCount = 4,
            RowCount = 1,
        };
        for (var i = 0; i < 4; i++)
        {
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }
        row.Controls.Add(BuildCounterColumn(_windowsValueLabel, "Windows"), 0, 0);
        row.Controls.Add(BuildCounterColumn(_choicesValueLabel, "Choices"), 1, 0);
        row.Controls.Add(BuildCounterColumn(_npcValueLabel, "NPC lines"), 2, 0);
        row.Controls.Add(BuildCounterColumn(_objectivesValueLabel, "Objectives"), 3, 0);
        return row;
    }

    private Control BuildCounterColumn(Label valueLabel, string caption)
    {
        var column = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = BackgroundColor,
            ColumnCount = 1,
            RowCount = 2,
        };
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 16));
        valueLabel.Dock = DockStyle.Fill;
        var captionLabel = new Label
        {
            Text = caption,
            AutoSize = false,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.TopCenter,
            ForeColor = Muted,
            Font = new Font("Segoe UI", 7.5F),
        };
        column.Controls.Add(valueLabel, 0, 0);
        column.Controls.Add(captionLabel, 0, 1);
        return column;
    }

    private static Label CreateCounterValueLabel() => new()
    {
        Text = "0",
        AutoSize = false,
        TextAlign = ContentAlignment.BottomCenter,
        Font = new Font("Segoe UI", 13F, FontStyle.Bold),
        ForeColor = ForegroundColor,
    };

    public void ApplyProgress(CaptureProgress update)
    {
        // ApplyProgress is only invoked while a capture session is active (including its
        // final Completed report), so the beacon's "capturing" gate is always satisfied here;
        // CaptureStopped() is what drives the idle state.
        var beacon = BeaconFor(update.State, update.Activity?.Kind, capturing: true);
        _beaconDot.BackColor = beacon.Color;
        _beaconLabel.Text = beacon.Text;

        var activity = update.Activity;
        if (activity != null && activity.Kind is "dialogue" or "transition" or "missionMessage" or "npcMessage")
        {
            _speakerLabel.Text = activity.Speaker ?? string.Empty;
            var excerpt = activity.Excerpt ?? string.Empty;
            if (!string.IsNullOrEmpty(activity.ArrivedVia))
            {
                excerpt = string.IsNullOrEmpty(excerpt)
                    ? $"via \"{activity.ArrivedVia}\""
                    : $"{excerpt}  via \"{activity.ArrivedVia}\"";
            }
            _excerptLabel.Text = excerpt;
        }

        if (activity?.Counters is { } counters)
        {
            _windowsValueLabel.Text = counters.Dialogues.ToString();
            _choicesValueLabel.Text = counters.Transitions.ToString();
            _npcValueLabel.Text = counters.NpcLines.ToString();
            _objectivesValueLabel.Text = counters.ProgressSnapshots.ToString();
        }
    }

    public void CaptureStarted()
    {
        _startedAt = DateTime.Now;
        _elapsedLabel.Text = "00:00:00";
        _elapsedTimer.Start();
        var beacon = BeaconFor(CaptureState.Starting, null, true);
        _beaconDot.BackColor = beacon.Color;
        _beaconLabel.Text = beacon.Text;
    }

    public void CaptureStopped()
    {
        _elapsedTimer.Stop();
        var beacon = BeaconFor(CaptureState.Completed, null, true);
        _beaconDot.BackColor = beacon.Color;
        _beaconLabel.Text = beacon.Text;
    }

    internal static (string Text, Color Color) BeaconFor(CaptureState state, string? activityKind, bool capturing)
    {
        if (!capturing)
        {
            return ("Idle - not capturing", Muted);
        }
        if (state == CaptureState.Completed)
        {
            return ("Capture complete", Success);
        }
        if (state is CaptureState.Scanning or CaptureState.Starting)
        {
            return ("Scanning...", Accent);
        }
        if (activityKind == "closed" || state == CaptureState.Waiting)
        {
            return ("Waiting for next dialogue", Warning);
        }
        if (activityKind is "dialogue" or "transition")
        {
            return ("Dialogue locked - safe to click through", BrightGreen);
        }
        return ("Capturing", Success);
    }

    private void PositionInitial()
    {
        var virtualBounds = SystemInformation.VirtualScreen;
        if (_settings.OverlayX.HasValue && _settings.OverlayY.HasValue)
        {
            var x = _settings.OverlayX.Value;
            var y = _settings.OverlayY.Value;
            const int minVisible = 100;
            const int minVisibleHeight = 40;
            x = Math.Max(virtualBounds.Left - Width + minVisible, Math.Min(x, virtualBounds.Right - minVisible));
            y = Math.Max(virtualBounds.Top - Height + minVisibleHeight, Math.Min(y, virtualBounds.Bottom - minVisibleHeight));
            StartPosition = FormStartPosition.Manual;
            Location = new Point(x, y);
        }
        else
        {
            var workingArea = Screen.PrimaryScreen?.WorkingArea ?? virtualBounds;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(workingArea.Right - Width - 24, workingArea.Top + 24);
        }
    }

    private void AttachDragHandlers(Control control)
    {
        if (control is Button)
        {
            return;
        }
        control.MouseDown += OnDragMouseDown;
        control.MouseMove += OnDragMouseMove;
        control.MouseUp += OnDragMouseUp;
        foreach (Control child in control.Controls)
        {
            AttachDragHandlers(child);
        }
    }

    private void OnDragMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }
        _dragging = true;
        _dragOffset = Cursor.Position - (Size)Location;
    }

    private void OnDragMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }
        Location = Cursor.Position - (Size)_dragOffset;
    }

    private void OnDragMouseUp(object? sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }
        _dragging = false;
        if (_persistEnabled)
        {
            _settings.OverlayX = Location.X;
            _settings.OverlayY = Location.Y;
            _settings.Save(GuiSettings.DefaultPath());
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _elapsedTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
