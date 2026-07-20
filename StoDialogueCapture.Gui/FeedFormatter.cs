namespace StoDialogueCapture.Gui;

internal enum FeedStyle
{
    Tag,
    Speaker,
    Body,
    Choice,
    ChoiceSelected,
    Muted,
}

internal readonly record struct FeedSegment(string Text, FeedStyle Style);

internal static class FeedFormatter
{
    public static IReadOnlyList<FeedSegment> Format(CaptureActivity activity, DateTime timestamp)
    {
        var segments = new List<FeedSegment>();
        segments.Add(new FeedSegment($"[{timestamp:HH:mm:ss}] [{KindTag(activity.Kind)}] ", FeedStyle.Tag));

        switch (activity.Kind)
        {
            case "dialogue":
                FormatDialogue(activity, segments);
                break;
            case "transition":
                FormatTransition(activity, segments);
                break;
            case "missionMessage":
            case "npcMessage":
                FormatMessage(activity, segments);
                break;
            case "missionProgress":
                FormatMissionProgress(activity, segments);
                break;
            case "closed":
                segments.Add(new FeedSegment("dialogue closed - waiting for the next one" + Environment.NewLine, FeedStyle.Muted));
                break;
            case "reacquired":
                FormatReacquired(activity, segments);
                break;
            default:
                if (!string.IsNullOrEmpty(activity.Excerpt))
                {
                    segments.Add(new FeedSegment(activity.Excerpt, FeedStyle.Body));
                }
                TerminateLine(segments);
                break;
        }

        return segments;
    }

    private static string KindTag(string kind) => kind switch
    {
        "dialogue" => "DIALOG",
        "transition" => "CHOICE",
        "missionMessage" => "MISSION",
        "npcMessage" => "NPC",
        "missionProgress" => "PROGRESS",
        "closed" => "CLOSED",
        "reacquired" => "REACQ",
        _ => kind.ToUpperInvariant(),
    };

    private static void FormatDialogue(CaptureActivity activity, List<FeedSegment> segments)
    {
        if (!string.IsNullOrEmpty(activity.Speaker))
        {
            segments.Add(new FeedSegment($"{activity.Speaker}: ", FeedStyle.Speaker));
        }
        if (!string.IsNullOrEmpty(activity.Excerpt))
        {
            segments.Add(new FeedSegment(activity.Excerpt, FeedStyle.Body));
        }
        if (!string.IsNullOrEmpty(activity.ArrivedVia))
        {
            segments.Add(new FeedSegment($"  via \"{activity.ArrivedVia}\"", FeedStyle.Muted));
        }
        TerminateLine(segments);

        if (activity.Choices is { Count: > 0 } choices)
        {
            foreach (var choice in choices)
            {
                var isSelected = !string.IsNullOrEmpty(activity.SelectedChoice) &&
                    string.Equals(choice, activity.SelectedChoice, StringComparison.OrdinalIgnoreCase);
                var text = isSelected ? $"  - {choice} (selected)" : $"  - {choice}";
                segments.Add(new FeedSegment(text + Environment.NewLine, isSelected ? FeedStyle.ChoiceSelected : FeedStyle.Choice));
            }
        }
    }

    private static void FormatTransition(CaptureActivity activity, List<FeedSegment> segments)
    {
        segments.Add(new FeedSegment("selected ", FeedStyle.Muted));
        if (!string.IsNullOrEmpty(activity.SelectedChoice))
        {
            segments.Add(new FeedSegment(activity.SelectedChoice, FeedStyle.ChoiceSelected));
        }
        segments.Add(new FeedSegment($" -> {activity.Speaker}", FeedStyle.Muted));
        TerminateLine(segments);
    }

    private static void FormatMessage(CaptureActivity activity, List<FeedSegment> segments)
    {
        if (!string.IsNullOrEmpty(activity.Speaker))
        {
            segments.Add(new FeedSegment($"{activity.Speaker}: ", FeedStyle.Speaker));
        }
        if (!string.IsNullOrEmpty(activity.Excerpt))
        {
            segments.Add(new FeedSegment(activity.Excerpt, FeedStyle.Body));
        }
        TerminateLine(segments);
    }

    private static void FormatMissionProgress(CaptureActivity activity, List<FeedSegment> segments)
    {
        if (!string.IsNullOrEmpty(activity.Speaker))
        {
            segments.Add(new FeedSegment($"{activity.Speaker}: ", FeedStyle.Speaker));
        }
        if (!string.IsNullOrEmpty(activity.Excerpt))
        {
            segments.Add(new FeedSegment(activity.Excerpt, FeedStyle.Body));
        }
        TerminateLine(segments);
    }

    private static void FormatReacquired(CaptureActivity activity, List<FeedSegment> segments)
    {
        var text = "reacquired";
        if (!string.IsNullOrEmpty(activity.Speaker))
        {
            text += $" {activity.Speaker}";
        }
        segments.Add(new FeedSegment(text + Environment.NewLine, FeedStyle.Muted));
    }

    private static void TerminateLine(List<FeedSegment> segments)
    {
        var last = segments[^1];
        segments[^1] = last with { Text = last.Text + Environment.NewLine };
    }
}
