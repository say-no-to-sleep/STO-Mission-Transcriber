using System.Diagnostics;
using System.Drawing;

namespace StoDialogueCapture.Gui;

internal sealed class MainForm : Form
{
    private static readonly Color Accent = Color.FromArgb(0, 137, 139);
    private static readonly Color AccentDark = Color.FromArgb(0, 75, 78);
    private static readonly Color Success = Color.FromArgb(30, 130, 76);
    private static readonly Color Warning = Color.FromArgb(191, 120, 20);
    private static readonly Color Muted = Color.FromArgb(94, 103, 112);

    private readonly TextBox _anchorBox = new() { Text = "Mission Simulator" };
    private readonly TextBox _outputBox = new() { Text = UiSupport.DefaultOutputPath() };
    private readonly TextBox _processBox = new() { Text = "GameClient" };
    private readonly NumericUpDown _pollBox = new()
    {
        Minimum = 50,
        Maximum = 60_000,
        Increment = 50,
        Value = 250,
        Width = 100,
    };
    private readonly CheckBox _chatLogBox = new()
    {
        Text = "Include Mission and ambient NPC messages from STO's chat log",
        Checked = true,
        AutoSize = true,
    };
    private readonly Label _gameStatus = new() { AutoSize = true };
    private readonly Label _chatStatus = new() { AutoSize = true };
    private readonly Label _captureStatus = new()
    {
        Text = "Ready — open the first dialogue before starting.",
        AutoSize = true,
        ForeColor = Muted,
    };
    private readonly RichTextBox _logBox = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = Color.FromArgb(18, 24, 27),
        ForeColor = Color.FromArgb(214, 235, 230),
        BorderStyle = BorderStyle.None,
        Font = new Font("Cascadia Mono", 9F),
        DetectUrls = false,
    };
    private readonly RichTextBox _rawLogBox = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        BackColor = Color.FromArgb(18, 24, 27),
        ForeColor = Color.FromArgb(214, 235, 230),
        BorderStyle = BorderStyle.None,
        Font = new Font("Cascadia Mono", 9F),
        DetectUrls = false,
        Visible = false,
    };
    private readonly CheckBox _showRawLogBox = new()
    {
        Text = "Show raw log",
        AutoSize = true,
    };
    private readonly Font _feedFont = new("Cascadia Mono", 9F);
    private readonly Font _feedFontBold = new("Cascadia Mono", 9F, FontStyle.Bold);
    private readonly ProgressBar _progressBar = new()
    {
        Dock = DockStyle.Top,
        Height = 5,
        Style = ProgressBarStyle.Blocks,
    };
    private readonly Button _startButton = CreatePrimaryButton("Start capture");
    private readonly Button _stopButton = CreateSecondaryButton("Stop and create transcript");
    private readonly Button _renderButton = CreateSecondaryButton("Render existing JSONL");
    private readonly Button _browseButton = CreateSecondaryButton("Browse...");
    private readonly Button _openTranscriptButton = CreateSecondaryButton("Open transcript");
    private readonly Button _openFolderButton = CreateSecondaryButton("Open output folder");
    private readonly System.Windows.Forms.Timer _readinessTimer = new() { Interval = 1500 };

    private CancellationTokenSource? _captureCancellation;
    private Task<CaptureRunResult>? _captureTask;
    private string? _lastTranscriptPath;
    private string? _lastOutputPath;
    private bool _closeAfterStop;

    public MainForm()
    {
        Text = "STO Dialogue Capture";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(860, 680);
        Size = new Size(980, 780);
        BackColor = Color.FromArgb(244, 247, 248);
        ForeColor = Color.FromArgb(27, 37, 42);
        Font = new Font("Segoe UI", 9.5F);

        Controls.Add(BuildLayout());
        AcceptButton = _startButton;

        _startButton.Click += StartCaptureAsync;
        _stopButton.Click += (_, _) => StopCapture();
        _renderButton.Click += RenderExistingAsync;
        _browseButton.Click += BrowseOutput;
        _openTranscriptButton.Click += (_, _) => OpenPath(_lastTranscriptPath);
        _openFolderButton.Click += (_, _) => OpenOutputFolder();
        _processBox.TextChanged += (_, _) => UpdateReadiness();
        _readinessTimer.Tick += (_, _) => UpdateReadiness();
        _readinessTimer.Start();
        FormClosing += OnFormClosing;
        _showRawLogBox.CheckedChanged += (_, _) =>
        {
            _rawLogBox.Visible = _showRawLogBox.Checked;
            _logBox.Visible = !_showRawLogBox.Checked;
        };

        _stopButton.Enabled = false;
        _openTranscriptButton.Enabled = false;
        _openFolderButton.Enabled = false;
        UpdateReadiness();
        LogBoth("Ready. In STO, run /ChatLog 1 and open the mission's first dialogue.");
    }

    private Control BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20),
            ColumnCount = 1,
            RowCount = 6,
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildReadinessPanel(), 0, 1);
        root.Controls.Add(BuildSettingsPanel(), 0, 2);
        root.Controls.Add(BuildActionPanel(), 0, 3);
        root.Controls.Add(BuildActivityPanel(), 0, 4);
        root.Controls.Add(BuildFooter(), 0, 5);
        return root;
    }

    private Control BuildHeader()
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = AccentDark,
            Padding = new Padding(18, 8, 18, 6),
            ColumnCount = 1,
            RowCount = 2,
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        var title = new Label
        {
            Text = "STO Dialogue Capture",
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 18F),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var subtitle = new Label
        {
            Text = "Read-only mission dialogue, choices, Mission updates, and NPC speech",
            ForeColor = Color.FromArgb(195, 224, 222),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        panel.Controls.Add(title, 0, 0);
        panel.Controls.Add(subtitle, 0, 1);
        return panel;
    }

    private Control BuildReadinessPanel()
    {
        var group = CreateGroup("Readiness");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 4, 10, 4),
            ColumnCount = 2,
            RowCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.Controls.Add(_gameStatus, 0, 0);
        layout.Controls.Add(_chatStatus, 1, 0);
        var instruction = new Label
        {
            Text = "Before Start: type /ChatLog 1 in STO, then leave the first dialogue open until it appears below.",
            AutoSize = true,
            ForeColor = Muted,
            Margin = new Padding(0, 8, 0, 0),
        };
        layout.Controls.Add(instruction, 0, 1);
        layout.SetColumnSpan(instruction, 2);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildSettingsPanel()
    {
        var group = CreateGroup("Capture settings");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 5, 10, 8),
            ColumnCount = 4,
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 37));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));

        layout.Controls.Add(CreateFieldLabel("Anchor"), 0, 0);
        _anchorBox.Dock = DockStyle.Fill;
        layout.Controls.Add(_anchorBox, 1, 0);
        layout.SetColumnSpan(_anchorBox, 3);

        layout.Controls.Add(CreateFieldLabel("Output"), 0, 1);
        _outputBox.Dock = DockStyle.Fill;
        layout.Controls.Add(_outputBox, 1, 1);
        layout.SetColumnSpan(_outputBox, 2);
        _browseButton.Dock = DockStyle.Fill;
        layout.Controls.Add(_browseButton, 3, 1);

        _chatLogBox.Margin = new Padding(3, 8, 3, 3);
        layout.Controls.Add(_chatLogBox, 1, 2);
        layout.SetColumnSpan(_chatLogBox, 3);

        layout.Controls.Add(CreateFieldLabel("Process"), 0, 3);
        _processBox.Width = 180;
        layout.Controls.Add(_processBox, 1, 3);
        layout.Controls.Add(CreateFieldLabel("Poll (ms)"), 2, 3);
        layout.Controls.Add(_pollBox, 3, 3);

        group.Controls.Add(layout);
        return group;
    }

    private Control BuildActionPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 4),
        };
        panel.Controls.Add(_startButton);
        panel.Controls.Add(_stopButton);
        panel.Controls.Add(_renderButton);
        return panel;
    }

    private Control BuildActivityPanel()
    {
        var group = CreateGroup("Live activity");
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(10, 4, 10, 10),
            ColumnCount = 1,
            RowCount = 3,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 7));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var headerRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        headerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _showRawLogBox.Anchor = AnchorStyles.Right;
        headerRow.Controls.Add(_captureStatus, 0, 0);
        headerRow.Controls.Add(_showRawLogBox, 1, 0);

        var logPanel = new Panel { Dock = DockStyle.Fill };
        logPanel.Controls.Add(_logBox);
        logPanel.Controls.Add(_rawLogBox);

        layout.Controls.Add(headerRow, 0, 0);
        layout.Controls.Add(_progressBar, 0, 1);
        layout.Controls.Add(logPanel, 0, 2);
        group.Controls.Add(layout);
        return group;
    }

    private Control BuildFooter()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 7, 0, 0),
        };
        panel.Controls.Add(_openFolderButton);
        panel.Controls.Add(_openTranscriptButton);
        return panel;
    }

    private async void StartCaptureAsync(object? sender, EventArgs eventArgs)
    {
        if (_captureTask is { IsCompleted: false })
        {
            return;
        }
        if (!ValidateCaptureInputs(out var outputPath))
        {
            return;
        }

        var readiness = UiSupport.GetReadiness(_processBox.Text.Trim());
        if (!readiness.GameRunning)
        {
            MessageBox.Show(this, "Start Star Trek Online before beginning capture.", "Game not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (File.Exists(outputPath) && MessageBox.Show(
                this,
                "That JSONL file already exists. New events will be appended to it. Continue?",
                "Append to existing capture",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        var options = new CaptureOptions(
            _processBox.Text.Trim(),
            _anchorBox.Text.Trim(),
            outputPath,
            decimal.ToInt32(_pollBox.Value),
            _chatLogBox.Checked,
            null,
            null,
            null,
            true,
            false,
            false);

        _captureCancellation = new CancellationTokenSource();
        SetCapturing(true);
        LogBoth($"Starting capture: {outputPath}");
        _captureTask = Task.Run(() => CaptureSession.RunAsync(
            options,
            OnCaptureProgress,
            _captureCancellation.Token));

        try
        {
            var result = await _captureTask;
            _lastOutputPath = result.JsonlPath;
            _lastTranscriptPath = result.Transcript?.OutputPath;
            _openFolderButton.Enabled = true;
            _openTranscriptButton.Enabled = _lastTranscriptPath != null && File.Exists(_lastTranscriptPath);
            SetStatus("Capture complete — JSONL and transcript are ready.", CaptureState.Completed);
            if (result.Transcript != null)
            {
                LogBoth(
                    $"Transcript: {result.Transcript.DialogueCount} dialogue window(s), " +
                    $"{result.Transcript.MissionMessageCount} Mission message(s), " +
                    $"{result.Transcript.NpcMessageCount} standalone NPC message(s).");
            }
            _outputBox.Text = UiSupport.DefaultOutputPath();
        }
        catch (Exception exception)
        {
            LogBoth($"ERROR: {exception.Message}");
            SetStatus("Capture failed — see the activity log.", CaptureState.Waiting);
            MessageBox.Show(this, exception.Message, "Capture failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _captureCancellation?.Dispose();
            _captureCancellation = null;
            SetCapturing(false);
            if (_closeAfterStop)
            {
                BeginInvoke(Close);
            }
        }
    }

    private void StopCapture()
    {
        if (_captureTask is not { IsCompleted: false } || _captureCancellation == null)
        {
            return;
        }
        _stopButton.Enabled = false;
        _captureCancellation.Cancel();
        SetStatus("Stopping after the current scan and creating the transcript...", CaptureState.Rendering);
        AppendLog("Stop requested. Please wait for transcript generation.");
    }

    private async void RenderExistingAsync(object? sender, EventArgs eventArgs)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select a dialogue capture",
            Filter = "JSON Lines capture (*.jsonl)|*.jsonl|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SetBusyForRender(true);
        AppendLog($"Rendering existing capture: {dialog.FileName}");
        try
        {
            var result = await Task.Run(() => TranscriptRenderer.Render(dialog.FileName));
            _lastOutputPath = dialog.FileName;
            _lastTranscriptPath = result.OutputPath;
            _openFolderButton.Enabled = true;
            _openTranscriptButton.Enabled = true;
            SetStatus("Existing capture rendered successfully.", CaptureState.Completed);
            AppendLog($"Transcript written to {result.OutputPath}");
        }
        catch (Exception exception)
        {
            LogBoth($"ERROR: {exception.Message}");
            SetStatus("Rendering failed — see the activity log.", CaptureState.Waiting);
            MessageBox.Show(this, exception.Message, "Rendering failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusyForRender(false);
        }
    }

    private void OnCaptureProgress(CaptureProgress update)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }
        BeginInvoke(() =>
        {
            SetStatus(update.Message, update.State);
            AppendLog(update.Message);
            if (update.Activity != null)
            {
                AppendFeed(FeedFormatter.Format(update.Activity, DateTime.Now));
            }
            else if (update.State is CaptureState.Starting or CaptureState.Waiting or CaptureState.Rendering or CaptureState.Completed ||
                     update.Message.StartsWith("ERROR", StringComparison.Ordinal))
            {
                AppendFeed(new[]
                {
                    new FeedSegment($"[{DateTime.Now:HH:mm:ss}] {update.Message}{Environment.NewLine}", FeedStyle.Muted),
                });
            }
        });
    }

    private void SetStatus(string text, CaptureState state)
    {
        _captureStatus.Text = text;
        _captureStatus.ForeColor = state switch
        {
            CaptureState.Completed => Success,
            CaptureState.Waiting => Warning,
            CaptureState.Scanning or CaptureState.Rendering => Accent,
            _ => Muted,
        };
        _progressBar.Style = state is CaptureState.Starting or CaptureState.Scanning or CaptureState.Rendering
            ? ProgressBarStyle.Marquee
            : ProgressBarStyle.Blocks;
        _progressBar.MarqueeAnimationSpeed = _progressBar.Style == ProgressBarStyle.Marquee ? 30 : 0;
    }

    private void SetCapturing(bool capturing)
    {
        _startButton.Enabled = !capturing;
        _stopButton.Enabled = capturing;
        _renderButton.Enabled = !capturing;
        _anchorBox.Enabled = !capturing;
        _outputBox.Enabled = !capturing;
        _browseButton.Enabled = !capturing;
        _processBox.Enabled = !capturing;
        _pollBox.Enabled = !capturing;
        _chatLogBox.Enabled = !capturing;
        if (!capturing && _progressBar.Style == ProgressBarStyle.Marquee)
        {
            _progressBar.Style = ProgressBarStyle.Blocks;
        }
    }

    private void SetBusyForRender(bool busy)
    {
        _startButton.Enabled = !busy;
        _renderButton.Enabled = !busy;
        _progressBar.Style = busy ? ProgressBarStyle.Marquee : ProgressBarStyle.Blocks;
        _progressBar.MarqueeAnimationSpeed = busy ? 30 : 0;
        if (busy)
        {
            SetStatus("Rendering the selected JSONL capture...", CaptureState.Rendering);
        }
    }

    private bool ValidateCaptureInputs(out string outputPath)
    {
        outputPath = _outputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(_anchorBox.Text))
        {
            MessageBox.Show(this, "Enter the visible name or header of the open first dialogue.", "First speaker required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _anchorBox.Focus();
            return false;
        }
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            MessageBox.Show(this, "Choose a JSONL output file.", "Output required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _outputBox.Focus();
            return false;
        }
        if (!outputPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            outputPath += ".jsonl";
            _outputBox.Text = outputPath;
        }
        try
        {
            outputPath = Path.GetFullPath(outputPath);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Invalid output path", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    private void BrowseOutput(object? sender, EventArgs eventArgs)
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save raw dialogue capture",
            Filter = "JSON Lines capture (*.jsonl)|*.jsonl",
            DefaultExt = "jsonl",
            AddExtension = true,
            FileName = Path.GetFileName(_outputBox.Text),
            InitialDirectory = Path.GetDirectoryName(_outputBox.Text),
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _outputBox.Text = dialog.FileName;
        }
    }

    private void UpdateReadiness()
    {
        var processName = _processBox.Text.Trim();
        if (processName.Length == 0)
        {
            _gameStatus.Text = "● Process name is empty";
            _gameStatus.ForeColor = Warning;
            _chatStatus.Text = "● Chat log unknown";
            _chatStatus.ForeColor = Muted;
            return;
        }

        var status = UiSupport.GetReadiness(processName);
        _gameStatus.Text = $"● {status.GameText}";
        _gameStatus.ForeColor = status.GameRunning ? Success : Warning;
        _chatStatus.Text = $"● {status.ChatText}";
        _chatStatus.ForeColor = status.ChatLogFound ? Success : Warning;
    }

    private void OpenOutputFolder()
    {
        var path = _lastOutputPath ?? _outputBox.Text;
        var directory = Path.GetDirectoryName(path);
        if (directory != null)
        {
            OpenPath(directory);
        }
    }

    private void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            MessageBox.Show(this, "The requested file or folder does not exist yet.", "Not found", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not open path", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void AppendLog(string message)
    {
        _rawLogBox.AppendText($"[{DateTime.Now:T}] {message}{Environment.NewLine}");
        _rawLogBox.SelectionStart = _rawLogBox.TextLength;
        _rawLogBox.ScrollToCaret();
    }

    private void AppendFeed(IReadOnlyList<FeedSegment> segments)
    {
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment.Text))
            {
                continue;
            }
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.SelectionLength = 0;
            _logBox.SelectionColor = FeedSegmentColor(segment.Style);
            _logBox.SelectionFont = FeedSegmentFont(segment.Style);
            _logBox.AppendText(segment.Text);
        }
        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.ScrollToCaret();
    }

    private void LogBoth(string message)
    {
        AppendLog(message);
        AppendFeed(new[]
        {
            new FeedSegment($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}", FeedStyle.Muted),
        });
    }

    private Color FeedSegmentColor(FeedStyle style) => style switch
    {
        FeedStyle.Tag => Color.FromArgb(120, 144, 156),
        FeedStyle.Speaker => Color.FromArgb(214, 235, 230),
        FeedStyle.Body => Color.FromArgb(214, 235, 230),
        FeedStyle.Choice => Color.FromArgb(144, 164, 174),
        FeedStyle.ChoiceSelected => Color.FromArgb(77, 208, 196),
        FeedStyle.Muted => Color.FromArgb(120, 134, 140),
        _ => Color.FromArgb(214, 235, 230),
    };

    private Font FeedSegmentFont(FeedStyle style) => style switch
    {
        FeedStyle.Speaker => _feedFontBold,
        FeedStyle.ChoiceSelected => _feedFontBold,
        _ => _feedFont,
    };

    private void OnFormClosing(object? sender, FormClosingEventArgs eventArgs)
    {
        if (_captureTask is not { IsCompleted: false })
        {
            return;
        }
        if (_closeAfterStop)
        {
            eventArgs.Cancel = true;
            return;
        }
        if (MessageBox.Show(
                this,
                "A capture is still running. Stop it, create the transcript, and close?",
                "Capture in progress",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question) != DialogResult.Yes)
        {
            eventArgs.Cancel = true;
            return;
        }

        eventArgs.Cancel = true;
        _closeAfterStop = true;
        StopCapture();
    }

    private static GroupBox CreateGroup(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = Color.FromArgb(38, 53, 59),
        Padding = new Padding(8),
    };

    private static Label CreateFieldLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Muted,
    };

    private static Button CreatePrimaryButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Height = 34,
        Padding = new Padding(18, 3, 18, 3),
        BackColor = Accent,
        ForeColor = Color.White,
        FlatStyle = FlatStyle.Flat,
        UseVisualStyleBackColor = false,
    };

    private static Button CreateSecondaryButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Height = 34,
        Padding = new Padding(12, 3, 12, 3),
        FlatStyle = FlatStyle.System,
    };
}
