using System.Diagnostics;

namespace StoDialogueCapture.Gui;

internal sealed record ReadinessStatus(
    bool GameRunning,
    string GameText,
    bool ChatLogFound,
    string ChatText);

internal static class UiSupport
{
    public static string DefaultOutputPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "STO Dialogue Captures");
        return Path.Combine(directory, $"mission-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
    }

    public static ReadinessStatus GetReadiness(string processName)
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(processName);
        }
        catch (Exception exception)
        {
            return new ReadinessStatus(false, $"Unable to check game: {exception.Message}", false, "Chat log unknown");
        }

        using var process = processes.FirstOrDefault();
        foreach (var extra in processes.Skip(1))
        {
            extra.Dispose();
        }
        if (process == null)
        {
            return new ReadinessStatus(false, "GameClient is not running", false, "Start STO, then run /ChatLog 1");
        }

        try
        {
            var processPath = process.MainModule?.FileName;
            var chatPath = processPath == null ? null : FindNewestChatLog(processPath);
            if (chatPath == null)
            {
                return new ReadinessStatus(true, "GameClient is running", false, "No chat log found — run /ChatLog 1");
            }

            var age = DateTime.Now - File.GetLastWriteTime(chatPath);
            var chatText = age < TimeSpan.FromMinutes(5)
                ? $"Chat log active: {Path.GetFileName(chatPath)}"
                : $"Chat log found; run /ChatLog 1 this session ({Path.GetFileName(chatPath)})";
            return new ReadinessStatus(true, "GameClient is running", true, chatText);
        }
        catch
        {
            return new ReadinessStatus(
                true,
                "GameClient is running",
                false,
                "Chat-log status unavailable — run /ChatLog 1 before capture");
        }
    }

    private static string? FindNewestChatLog(string processPath)
    {
        var moduleDirectory = Path.GetDirectoryName(processPath);
        if (moduleDirectory == null)
        {
            return null;
        }
        var directoryName = Path.GetFileName(moduleDirectory);
        var liveDirectory = directoryName.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
                            directoryName.Equals("x86", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(moduleDirectory)?.FullName
            : moduleDirectory;
        if (liveDirectory == null)
        {
            return null;
        }

        var logDirectory = Path.Combine(liveDirectory, "logs", "GameClient");
        if (!Directory.Exists(logDirectory))
        {
            return null;
        }
        return Directory.EnumerateFiles(logDirectory, "chat_*.log")
            .Concat(Directory.EnumerateFiles(logDirectory, "chat.log"))
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }
}

internal static class UiSelfTests
{
    public static void Run()
    {
        var output = UiSupport.DefaultOutputPath();
        Assert(output.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase), "default JSONL extension");
        Assert(Path.IsPathFullyQualified(output), "default output is absolute");
        Assert(
            Path.GetFileName(output).StartsWith("mission-", StringComparison.Ordinal),
            "timestamped default filename");

        var readiness = UiSupport.GetReadiness("ProcessThatShouldNeverExist_StoCaptureTest");
        Assert(!readiness.GameRunning, "missing process readiness");

        using var form = new MainForm();
        var buttons = Descendants<Button>(form).Select(button => button.Text).ToHashSet();
        Assert(buttons.Contains("Start capture"), "Start capture button");
        Assert(buttons.Contains("Stop and create transcript"), "Stop capture button");
        Assert(buttons.Contains("Render existing JSONL"), "offline render button");
        Assert(buttons.Contains("Open transcript"), "open transcript button");

        var checkBoxes = Descendants<CheckBox>(form).Select(checkBox => checkBox.Text).ToHashSet();
        Assert(checkBoxes.Contains("Show raw log"), "show raw log checkbox");

        var richTextBoxCount = Descendants<RichTextBox>(form).Count();
        Assert(richTextBoxCount == 2, "two live activity text boxes");

        RunFeedFormatterSelfTests();
    }

    private static void RunFeedFormatterSelfTests()
    {
        var dialogueActivity = new CaptureActivity(
            "dialogue",
            "Tovan Khev",
            "Hello there.",
            new[] { "Accept", "Decline" },
            "Accept",
            "Continue",
            new CaptureCounters(0, 0, 0, 0, 0));
        var dialogueSegments = FeedFormatter.Format(dialogueActivity, DateTime.Now);
        Assert(
            dialogueSegments.Count > 0 && dialogueSegments[0].Text.StartsWith("[", StringComparison.Ordinal) &&
            dialogueSegments[0].Text.Contains("DIALOG", StringComparison.Ordinal),
            "dialogue feed starts with timestamp and DIALOG tag");
        Assert(
            dialogueSegments.Any(segment =>
                segment.Style == FeedStyle.ChoiceSelected &&
                segment.Text.Contains("Accept", StringComparison.Ordinal) &&
                segment.Text.Contains("(selected)", StringComparison.Ordinal)),
            "dialogue feed marks the selected choice");
        Assert(
            dialogueSegments.Any(segment =>
                segment.Style == FeedStyle.Muted && segment.Text.Contains("Continue", StringComparison.Ordinal)),
            "dialogue feed notes arrival via");
        Assert(
            dialogueSegments.All(segment => !string.IsNullOrEmpty(segment.Text)),
            "dialogue feed has no null/empty segments");

        var closedActivity = new CaptureActivity(
            "closed",
            null,
            null,
            null,
            null,
            null,
            new CaptureCounters(0, 0, 0, 0, 0));
        var closedSegments = FeedFormatter.Format(closedActivity, DateTime.Now);
        Assert(
            closedSegments.Any(segment =>
                segment.Style == FeedStyle.Muted && segment.Text.Contains("waiting", StringComparison.Ordinal)),
            "closed feed reports waiting for next dialogue");
    }

    private static IEnumerable<T> Descendants<T>(Control root) where T : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is T match)
            {
                yield return match;
            }
            foreach (var descendant in Descendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"UI self-test failed: {name}");
        }
    }
}
