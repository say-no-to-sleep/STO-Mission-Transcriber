using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StoDialogueCapture;

internal sealed record TranscriptRenderResult(
    string OutputPath,
    int DialogueCount,
    int MissionMessageCount,
    int NpcMessageCount,
    int MissionProgressCount,
    int SuppressedCount,
    int MalformedLineCount);

internal static partial class TranscriptRenderer
{
    private sealed record TranscriptEvent(
        int Sequence,
        DateTimeOffset? CapturedAt,
        string Event,
        string Speaker,
        string DialogHeader,
        string DialogText1,
        string DialogHeader2,
        string DialogText2,
        string ListHeader,
        int ScreenType,
        IReadOnlyList<TranscriptOption> Options,
        string Text,
        string InboundChoice,
        string SelectedChoice,
        bool Recovered,
        string MissionInternalName,
        string MissionTitle,
        int MissionState,
        string MissionStateName,
        IReadOnlyList<TranscriptMissionObjective> MissionObjectives);

    private sealed record TranscriptOption(
        string DisplayString,
        string DisplayString2,
        bool NeedsConfirm,
        string ConfirmHeader,
        string ConfirmText,
        bool InteractionDisabled,
        string InteractionDisabledMessage);

    private sealed record TranscriptMissionObjective(
        string InternalName,
        string DisplayText,
        int State,
        string StateName,
        int Count,
        int Target,
        int Depth,
        int DisplayOrder,
        bool VisibleInTracker,
        string ParentInternalName,
        int TraversalOrder,
        int TranscriptOrder);

    public static TranscriptRenderResult Render(string jsonlPath, string? outputPath = null)
    {
        var fullInputPath = Path.GetFullPath(jsonlPath);
        if (!File.Exists(fullInputPath))
        {
            throw new FileNotFoundException("JSONL capture was not found.", fullInputPath);
        }

        var fullOutputPath = Path.GetFullPath(
            outputPath ?? Path.ChangeExtension(fullInputPath, ".transcript.md"));
        if (string.Equals(fullInputPath, fullOutputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Transcript output must be different from the JSONL input.");
        }

        var events = new List<TranscriptEvent>();
        var malformedLines = 0;
        var sequence = 0;
        using var inputStream = new FileStream(
            fullInputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var input = new StreamReader(inputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (input.ReadLine() is { } line)
        {
            sequence++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var eventName = GetString(root, "event");
                var capturedAt = ParseTimestamp(GetString(root, "capturedAt"));
                switch (eventName)
                {
                    case "dialogue":
                        events.Add(ParseDialogue(sequence, capturedAt, root));
                        break;
                    case "missionMessage":
                    case "npcMessage":
                        events.Add(new TranscriptEvent(
                            sequence,
                            capturedAt,
                            eventName,
                            CleanText(GetString(root, "speaker")),
                            string.Empty,
                            string.Empty,
                            string.Empty,
                            string.Empty,
                            string.Empty,
                            0,
                            [],
                            CleanText(GetString(root, "text")),
                            string.Empty,
                            string.Empty,
                            false,
                            string.Empty,
                            string.Empty,
                            0,
                            string.Empty,
                            []));
                        break;
                    case "missionProgress":
                        events.Add(ParseMissionProgress(sequence, capturedAt, root));
                        break;
                }
            }
            catch (JsonException)
            {
                malformedLines++;
            }
        }

        var linkedEvents = ApplyMissionObjectiveHistoryOrdering(ApplySelectionLinks(events));
        var suppressed = 0;
        var uniqueDialogues = new List<TranscriptEvent>();
        string? previousDialogueFingerprint = null;
        foreach (var originalDialogue in linkedEvents.Where(item => item.Event == "dialogue"))
        {
            var dialogue = RecoverBodyOnlyDialogue(originalDialogue, linkedEvents) ?? originalDialogue;
            var fingerprint = DialogueFingerprint(dialogue);
            if (!IsReadableDialogue(dialogue))
            {
                suppressed++;
                continue;
            }
            if (string.Equals(fingerprint, previousDialogueFingerprint, StringComparison.Ordinal))
            {
                if (uniqueDialogues.Count > 0 && !string.IsNullOrWhiteSpace(dialogue.SelectedChoice))
                {
                    uniqueDialogues[^1] = uniqueDialogues[^1] with
                    {
                        SelectedChoice = dialogue.SelectedChoice,
                    };
                }
                suppressed++;
                continue;
            }
            uniqueDialogues.Add(dialogue);
            previousDialogueFingerprint = fingerprint;
        }

        var included = new List<TranscriptEvent>();
        var uniqueDialoguesBySequence = uniqueDialogues.ToDictionary(item => item.Sequence);
        string? previousMissionProgressFingerprint = null;
        var wrapperActivatedTitles = new HashSet<string>(StringComparer.Ordinal);
        var openMissionTitles = linkedEvents
            .Where(item => item.Event == "missionProgress" &&
                           item.MissionInternalName.EndsWith("_Om", StringComparison.OrdinalIgnoreCase))
            .Select(item => NormalizeComparison(item.MissionTitle))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var item in linkedEvents)
        {
            if (item.Event == "dialogue")
            {
                if (uniqueDialoguesBySequence.TryGetValue(item.Sequence, out var readableDialogue))
                {
                    included.Add(readableDialogue);
                }
                continue;
            }

            if (item.Event == "npcMessage" && IsDialogueExcerpt(item, uniqueDialogues))
            {
                suppressed++;
                continue;
            }

            if ((item.Event == "missionMessage" || item.Event == "npcMessage") &&
                HasLongerLogicalChatRecord(item, linkedEvents))
            {
                suppressed++;
                continue;
            }

            if (item.Event == "missionProgress")
            {
                var missionTitleKey = NormalizeComparison(item.MissionTitle);
                var isPrimaryWrapper = item.MissionInternalName.EndsWith(
                    "_Pm",
                    StringComparison.OrdinalIgnoreCase);
                var isOpenMission = item.MissionInternalName.EndsWith(
                    "_Om",
                    StringComparison.OrdinalIgnoreCase);
                var visibleWrapperObjectives = item.MissionObjectives
                    .Where(objective => objective.VisibleInTracker)
                    .ToList();
                if (isPrimaryWrapper && item.MissionState == 0 &&
                    visibleWrapperObjectives.Count > 0 &&
                    visibleWrapperObjectives.All(objective =>
                        openMissionTitles.Contains(NormalizeComparison(objective.DisplayText))))
                {
                    // Primary children often carry the title of the section
                    // they launch. If that label later becomes an OpenMission
                    // title, it was a section gate—not a HUD checkbox row.
                    suppressed++;
                    continue;
                }
                if (isOpenMission && wrapperActivatedTitles.Contains(missionTitleKey))
                {
                    // The old OpenMission can remain readable after STO advances
                    // the HUD to an episode-wrapper child such as Return to Sector
                    // Space. It is stale history, not a second tracker transition.
                    suppressed++;
                    continue;
                }
                if (isPrimaryWrapper &&
                    (item.MissionState != 0 ||
                     item.MissionObjectives.Any(objective => objective.VisibleInTracker)))
                {
                    wrapperActivatedTitles.Add(missionTitleKey);
                }
                var fingerprint = MissionProgressFingerprint(item);
                if (string.Equals(
                        fingerprint,
                        previousMissionProgressFingerprint,
                        StringComparison.Ordinal))
                {
                    suppressed++;
                    continue;
                }
                previousMissionProgressFingerprint = fingerprint;
                included.Add(item);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(item.Text))
            {
                included.Add(item);
            }
        }

        var chronological = included
            .OrderBy(item => item.CapturedAt ?? DateTimeOffset.MaxValue)
            .ThenBy(item => item.Sequence)
            .ToList();
        var title = InferMissionTitle(events);
        var markdown = BuildMarkdown(fullInputPath, title, chronological, suppressed, malformedLines);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        File.WriteAllText(fullOutputPath, markdown, new UTF8Encoding(false));

        return new TranscriptRenderResult(
            fullOutputPath,
            chronological.Count(item => item.Event == "dialogue"),
            chronological.Count(item => item.Event == "missionMessage"),
            chronological.Count(item => item.Event == "npcMessage"),
            chronological.Count(item => item.Event == "missionProgress"),
            suppressed,
            malformedLines);
    }

    private static TranscriptEvent ParseDialogue(
        int sequence,
        DateTimeOffset? capturedAt,
        JsonElement root)
    {
        var options = new List<TranscriptOption>();
        if (root.TryGetProperty("options", out var optionArray) &&
            optionArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in optionArray.EnumerateArray())
            {
                options.Add(new TranscriptOption(
                    CleanText(GetString(option, "displayString")),
                    CleanText(GetString(option, "displayString2")),
                    GetBoolean(option, "needsConfirm"),
                    CleanText(GetString(option, "confirmHeader")),
                    CleanText(GetString(option, "confirmText")),
                    GetBoolean(option, "interactionDisabled"),
                    CleanText(GetString(option, "interactionDisabledMessage"))));
            }
        }

        return new TranscriptEvent(
            sequence,
            capturedAt,
            "dialogue",
            CleanText(GetString(root, "contactDisplayName")),
            CleanText(GetString(root, "dialogHeader")),
            CleanText(GetString(root, "dialogText1")),
            CleanText(GetString(root, "dialogHeader2")),
            CleanText(GetString(root, "dialogText2")),
            CleanText(GetString(root, "listHeader")),
            GetInt32(root, "screenType"),
            options,
            string.Empty,
            CleanText(GetString(root, "lastResponseDisplayString")),
            string.Empty,
            false,
            string.Empty,
            string.Empty,
            0,
            string.Empty,
            []);
    }

    private static TranscriptEvent ParseMissionProgress(
        int sequence,
        DateTimeOffset? capturedAt,
        JsonElement root)
    {
        var objectives = new List<TranscriptMissionObjective>();
        if (root.TryGetProperty("objectives", out var objectiveArray) &&
            objectiveArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var objective in objectiveArray.EnumerateArray())
            {
                objectives.Add(new TranscriptMissionObjective(
                    GetString(objective, "internalName"),
                    CleanText(GetString(objective, "displayText")),
                    GetInt32(objective, "state"),
                    GetString(objective, "stateName"),
                    GetInt32(objective, "count"),
                    GetInt32(objective, "target"),
                    GetInt32(objective, "depth"),
                    GetInt32(objective, "displayOrder"),
                    GetBoolean(objective, "visibleInTracker"),
                    GetString(objective, "parentInternalName"),
                    GetInt32(objective, "traversalOrder"),
                    0));
            }
        }

        return new TranscriptEvent(
            sequence,
            capturedAt,
            "missionProgress",
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            [],
            string.Empty,
            string.Empty,
            string.Empty,
            false,
            GetString(root, "missionInternalName"),
            CleanText(GetString(root, "missionTitle")),
            GetInt32(root, "state"),
            GetString(root, "stateName"),
            objectives);
    }

    private static IReadOnlyList<TranscriptEvent> ApplySelectionLinks(
        IReadOnlyList<TranscriptEvent> sourceEvents)
    {
        var linked = sourceEvents.ToList();
        int? previousDialogueIndex = null;
        for (var index = 0; index < linked.Count; index++)
        {
            var current = linked[index];
            if (current.Event != "dialogue")
            {
                continue;
            }

            var inboundChoice = current.InboundChoice;
            var linkedToPrevious = false;
            if (previousDialogueIndex != null && !string.IsNullOrWhiteSpace(inboundChoice))
            {
                var previous = linked[previousDialogueIndex.Value];
                linkedToPrevious = previous.Options.Any(option =>
                    ChoiceTextEquals(option.DisplayString, inboundChoice) ||
                    ChoiceTextEquals(option.DisplayString2, inboundChoice));
                if (linkedToPrevious)
                {
                    linked[previousDialogueIndex.Value] = previous with { SelectedChoice = inboundChoice };
                }
            }

            if (!linkedToPrevious && !string.IsNullOrWhiteSpace(inboundChoice))
            {
                linked[index] = current with { InboundChoice = string.Empty };
            }
            previousDialogueIndex = index;
        }

        // A visible, enabled one-button dialogue has no route ambiguity. Mark
        // that button as selected even when STO replaces or closes the window
        // before LastResponseDisplayString can be sampled.
        for (var index = 0; index < linked.Count; index++)
        {
            var current = linked[index];
            if (current.Event != "dialogue" || !string.IsNullOrWhiteSpace(current.SelectedChoice))
            {
                continue;
            }
            var available = current.Options
                .Where(HasVisibleOptionText)
                .Where(option => !option.InteractionDisabled)
                .ToList();
            if (available.Count != 1) continue;
            var choice = FirstNonempty(
                available[0].DisplayString,
                available[0].DisplayString2,
                available[0].ConfirmHeader,
                available[0].ConfirmText);
            linked[index] = current with { SelectedChoice = choice };

            var nextDialogueIndex = linked.FindIndex(index + 1, item => item.Event == "dialogue");
            if (nextDialogueIndex < 0) continue;
            var next = linked[nextDialogueIndex];
            if (!string.IsNullOrWhiteSpace(next.InboundChoice) ||
                current.CapturedAt == null || next.CapturedAt == null ||
                (next.CapturedAt.Value - current.CapturedAt.Value).TotalSeconds is < 0 or > 12)
            {
                continue;
            }
            linked[nextDialogueIndex] = next with { InboundChoice = choice };
        }

        // Some terminal choices close the dialogue or change maps, so no next
        // ContactDialog exists to carry LastResponseDisplayString. Strong
        // mission outcomes can still identify a few exact choices without
        // guessing among unrelated alternatives.
        for (var index = 0; index < linked.Count; index++)
        {
            var current = linked[index];
            if (current.Event != "dialogue" || !string.IsNullOrWhiteSpace(current.SelectedChoice) ||
                current.CapturedAt == null)
            {
                continue;
            }
            var outcomeMessages = linked
                .Where(item => item.Event == "missionMessage" && item.CapturedAt != null)
                .Where(item =>
                    (item.CapturedAt!.Value - current.CapturedAt.Value).TotalSeconds is >= 0 and <= 30)
                .Select(item => item.Text)
                .ToList();
            string? inferred = null;
            if (outcomeMessages.Any(text =>
                    text.StartsWith("Acquired mission:", StringComparison.OrdinalIgnoreCase)))
            {
                inferred = FindOfferedChoice(current, "Accept");
            }
            if (inferred == null && outcomeMessages.Any(text =>
                    text.Contains("complete!", StringComparison.OrdinalIgnoreCase)))
            {
                inferred = FindOfferedChoice(current, "Leave System");
            }
            if (inferred == null && outcomeMessages.Any(text =>
                    text.StartsWith("Resolved mission", StringComparison.OrdinalIgnoreCase)))
            {
                inferred = FindOfferedChoice(current, "Collect Reward");
            }
            if (inferred != null)
            {
                linked[index] = current with { SelectedChoice = inferred };
            }
        }
        return linked;
    }

    private static string? FindOfferedChoice(TranscriptEvent dialogue, string expected)
    {
        foreach (var option in dialogue.Options.Where(option => !option.InteractionDisabled))
        {
            if (ChoiceTextEquals(option.DisplayString, expected)) return option.DisplayString;
            if (ChoiceTextEquals(option.DisplayString2, expected)) return option.DisplayString2;
        }
        return null;
    }

    private static IReadOnlyList<TranscriptEvent> ApplyMissionObjectiveHistoryOrdering(
        IReadOnlyList<TranscriptEvent> sourceEvents)
    {
        var ordered = sourceEvents.ToList();
        var firstSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        var nextOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < ordered.Count; index++)
        {
            var item = ordered[index];
            if (item.Event != "missionProgress") continue;
            var missionKey = NormalizeComparison(item.MissionTitle);
            nextOrder.TryGetValue(missionKey, out var next);
            var sourceOrder = item.MissionObjectives.Any(objective => objective.TraversalOrder > 0)
                ? item.MissionObjectives.OrderBy(objective => objective.TraversalOrder)
                : item.MissionObjectives
                    .OrderBy(objective => objective.DisplayOrder)
                    .ThenBy(objective => objective.Depth);
            foreach (var objective in sourceOrder.Where(objective => objective.VisibleInTracker))
            {
                var objectiveKey = string.Join(
                    "\u001f",
                    missionKey,
                    NormalizeComparison(objective.InternalName),
                    NormalizeComparison(objective.DisplayText));
                if (firstSeen.ContainsKey(objectiveKey)) continue;
                firstSeen[objectiveKey] = ++next;
            }
            nextOrder[missionKey] = next;
            ordered[index] = item with
            {
                MissionObjectives = item.MissionObjectives
                    .Select(objective =>
                    {
                        var objectiveKey = string.Join(
                            "\u001f",
                            missionKey,
                            NormalizeComparison(objective.InternalName),
                            NormalizeComparison(objective.DisplayText));
                        return objective with
                        {
                            TranscriptOrder = firstSeen.GetValueOrDefault(objectiveKey),
                        };
                    })
                    .ToList(),
            };
        }
        return ordered;
    }

    private static string BuildMarkdown(
        string sourcePath,
        string title,
        IReadOnlyList<TranscriptEvent> events,
        int suppressed,
        int malformedLines)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"# Mission Transcript: {EscapeInline(title)}");
        builder.AppendLine();
        builder.AppendLine($"Source: `{Path.GetFileName(sourcePath)}`  ");
        builder.AppendLine($"Generated: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine();
        builder.AppendLine(
            $"Captured {events.Count(item => item.Event == "dialogue")} dialogue windows, " +
            $"{events.Count(item => item.Event == "missionMessage")} mission message(s), and " +
            $"{events.Count(item => item.Event == "npcMessage")} standalone NPC message(s), with " +
            $"{events.Count(item => item.Event == "missionProgress")} mission-progress snapshot(s).");
        if (suppressed > 0 || malformedLines > 0)
        {
            builder.AppendLine();
            builder.Append(
                $"Cleanup: omitted {suppressed} duplicate, overlapping, or invalid capture artifact(s)");
            if (malformedLines > 0)
            {
                builder.Append($"; skipped {malformedLines} malformed JSONL line(s)");
            }
            builder.AppendLine(". The raw JSONL remains the complete audit record.");
        }

        foreach (var item in events)
        {
            builder.AppendLine();
            switch (item.Event)
            {
                case "dialogue":
                    AppendDialogue(builder, item);
                    break;
                case "missionMessage":
                    builder.AppendLine("## Mission update");
                    AppendTimestamp(builder, item.CapturedAt);
                    builder.AppendLine();
                    builder.AppendLine(item.Text);
                    break;
                case "npcMessage":
                    var speaker = string.IsNullOrWhiteSpace(item.Speaker) ? "Unknown speaker" : item.Speaker;
                    builder.AppendLine($"## NPC - {EscapeInline(speaker)}");
                    AppendTimestamp(builder, item.CapturedAt);
                    builder.AppendLine();
                    builder.AppendLine(item.Text);
                    break;
                case "missionProgress":
                    AppendMissionProgress(builder, item);
                    break;
            }
        }

        return builder.ToString();
    }

    private static void AppendMissionProgress(StringBuilder builder, TranscriptEvent item)
    {
        var title = string.IsNullOrWhiteSpace(item.MissionTitle) ? "Mission" : item.MissionTitle;
        builder.AppendLine($"## Mission progress - {EscapeInline(title)}");
        AppendTimestamp(builder, item.CapturedAt);
        builder.AppendLine();

        var visible = item.MissionObjectives
            .Where(objective => objective.VisibleInTracker)
            .OrderBy(objective => objective.TranscriptOrder)
            .ToList();
        if (visible.Count == 0)
        {
            builder.AppendLine($"Mission state: {EscapeInline(item.MissionStateName)}");
            return;
        }

        foreach (var objective in visible)
        {
            var marker = objective.State switch
            {
                1 => "[x]",
                2 => "[!]",
                _ => "[ ]",
            };
            var count = objective.Target > 0
                ? $" ({objective.Count}/{objective.Target})"
                : string.Empty;
            builder.AppendLine($"- {marker} {EscapeInline(objective.DisplayText)}{count}");
        }
    }

    private static void AppendDialogue(StringBuilder builder, TranscriptEvent item)
    {
        var speaker = string.IsNullOrWhiteSpace(item.Speaker) ? "Dialogue" : item.Speaker;
        builder.AppendLine($"## {EscapeInline(speaker)}");
        AppendTimestamp(builder, item.CapturedAt);
        if (item.Recovered)
        {
            builder.AppendLine();
            builder.AppendLine("_Recovered from an incomplete dialogue capture; visible choices were unavailable._");
        }
        if (!string.IsNullOrWhiteSpace(item.InboundChoice))
        {
            builder.AppendLine();
            builder.AppendLine($"**Arrived via choice:** {EscapeInline(item.InboundChoice)}");
        }
        if (!string.IsNullOrWhiteSpace(item.DialogHeader) &&
            !string.Equals(item.DialogHeader, item.Speaker, StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine();
            builder.AppendLine($"**{EscapeInline(item.DialogHeader)}**");
        }
        AppendBlock(builder, item.DialogText1);
        if (!string.IsNullOrWhiteSpace(item.DialogHeader2))
        {
            builder.AppendLine();
            builder.AppendLine($"**{EscapeInline(item.DialogHeader2)}**");
        }
        AppendBlock(builder, item.DialogText2);
        if (!string.IsNullOrWhiteSpace(item.ListHeader))
        {
            builder.AppendLine();
            builder.AppendLine($"**{EscapeInline(item.ListHeader)}**");
        }

        var visibleOptions = item.Options.Where(HasVisibleOptionText).ToList();
        if (visibleOptions.Count == 0)
        {
            if (!string.IsNullOrWhiteSpace(item.SelectedChoice))
            {
                builder.AppendLine();
                builder.AppendLine($"**Selected choice:** {EscapeInline(item.SelectedChoice)}");
            }
            return;
        }

        builder.AppendLine();
        builder.AppendLine("**Choices shown**");
        builder.AppendLine();
        foreach (var option in visibleOptions)
        {
            var label = FirstNonempty(option.DisplayString, option.DisplayString2, option.ConfirmHeader);
            var selectedMarker = ChoiceTextEquals(label, item.SelectedChoice) ? " **(selected)**" : string.Empty;
            builder.AppendLine($"- {EscapeInline(label)}{selectedMarker}");
            AppendIndented(builder, option.DisplayString2, label);
            AppendIndented(builder, option.ConfirmHeader, label);
            AppendIndented(builder, option.ConfirmText, label);
            if (option.InteractionDisabled && !string.IsNullOrWhiteSpace(option.InteractionDisabledMessage))
            {
                builder.AppendLine($"  - Unavailable: {EscapeInline(option.InteractionDisabledMessage)}");
            }
        }
    }

    private static void AppendTimestamp(StringBuilder builder, DateTimeOffset? timestamp)
    {
        if (timestamp != null)
        {
            builder.AppendLine();
            builder.AppendLine($"_{timestamp.Value.ToLocalTime():HH:mm:ss}_");
        }
    }

    private static void AppendBlock(StringBuilder builder, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        builder.AppendLine();
        builder.AppendLine(value);
    }

    private static void AppendIndented(StringBuilder builder, string value, string label)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            !string.Equals(value, label, StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine($"  - {EscapeInline(value)}");
        }
    }

    private static bool IsReadableDialogue(TranscriptEvent dialogue)
    {
        if (dialogue.ScreenType == 0 && dialogue.Options.Count == 0)
        {
            return false;
        }

        var content = string.Join(
            " ",
            dialogue.Speaker,
            dialogue.DialogHeader,
            dialogue.DialogText1,
            dialogue.DialogHeader2,
            dialogue.DialogText2,
            dialogue.ListHeader);
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        return !TechnicalResourcePattern().IsMatch(content);
    }

    private static TranscriptEvent? RecoverBodyOnlyDialogue(
        TranscriptEvent dialogue,
        IReadOnlyList<TranscriptEvent> events)
    {
        if (dialogue.ScreenType != 0 || dialogue.Options.Count != 0 ||
            !dialogue.DialogHeader.Equals("Rewardrow", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(dialogue.Speaker))
        {
            return null;
        }

        var body = dialogue.Speaker;
        var normalizedBody = NormalizeComparison(body);
        var matchingNpc = events
            .Where(item => item.Event == "npcMessage" && !string.IsNullOrWhiteSpace(item.Speaker))
            .Where(item => item.CapturedAt == null || dialogue.CapturedAt == null ||
                Math.Abs((item.CapturedAt.Value - dialogue.CapturedAt.Value).TotalSeconds) <= 30)
            .Where(item => normalizedBody.Contains(NormalizeComparison(item.Text), StringComparison.Ordinal))
            .OrderBy(item => item.CapturedAt == null || dialogue.CapturedAt == null
                ? double.MaxValue
                : Math.Abs((item.CapturedAt.Value - dialogue.CapturedAt.Value).TotalSeconds))
            .FirstOrDefault();
        if (matchingNpc == null)
        {
            return null;
        }

        return dialogue with
        {
            Speaker = matchingNpc.Speaker,
            DialogHeader = string.Empty,
            DialogText1 = body,
            DialogHeader2 = string.Empty,
            DialogText2 = string.Empty,
            ListHeader = string.Empty,
            ScreenType = 2,
            Recovered = true,
        };
    }

    private static bool IsDialogueExcerpt(
        TranscriptEvent npcMessage,
        IReadOnlyList<TranscriptEvent> dialogues)
    {
        var excerpt = NormalizeComparison(npcMessage.Text);
        if (excerpt.Length < 12)
        {
            return false;
        }

        foreach (var dialogue in dialogues)
        {
            if (!SpeakersMatch(npcMessage.Speaker, dialogue.Speaker))
            {
                continue;
            }
            if (npcMessage.CapturedAt != null && dialogue.CapturedAt != null &&
                Math.Abs((dialogue.CapturedAt.Value - npcMessage.CapturedAt.Value).TotalSeconds) > 120)
            {
                continue;
            }

            var dialogueText = NormalizeComparison(string.Join(
                " ",
                dialogue.DialogText1,
                dialogue.DialogText2,
                dialogue.ListHeader));
            if (dialogueText.Contains(excerpt, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static bool HasLongerLogicalChatRecord(
        TranscriptEvent candidate,
        IReadOnlyList<TranscriptEvent> events)
    {
        if (candidate.CapturedAt == null) return false;
        var candidateText = NormalizeComparison(candidate.Text);
        if (candidateText.Length == 0) return false;
        var candidateSpeaker = NormalizeComparison(candidate.Speaker);
        return events.Any(other =>
            other.Sequence != candidate.Sequence &&
            other.Event == candidate.Event &&
            other.CapturedAt == candidate.CapturedAt &&
            NormalizeComparison(other.Speaker) == candidateSpeaker &&
            NormalizeComparison(other.Text).Length > candidateText.Length &&
            NormalizeComparison(other.Text).StartsWith(
                candidateText + " ",
                StringComparison.Ordinal));
    }

    private static bool SpeakersMatch(string left, string right) =>
        string.IsNullOrWhiteSpace(left) ||
        string.IsNullOrWhiteSpace(right) ||
        string.Equals(NormalizeComparison(left), NormalizeComparison(right), StringComparison.Ordinal);

    private static bool ChoiceTextEquals(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(NormalizeComparison(left), NormalizeComparison(right), StringComparison.Ordinal);

    private static string DialogueFingerprint(TranscriptEvent dialogue) => NormalizeComparison(string.Join(
        "\u001f",
        dialogue.Speaker,
        dialogue.DialogHeader,
        dialogue.DialogText1,
        dialogue.DialogHeader2,
        dialogue.DialogText2,
        dialogue.ListHeader,
        string.Join("\u001e", dialogue.Options.Select(option =>
            string.Join("\u001d", option.DisplayString, option.DisplayString2, option.ConfirmHeader, option.ConfirmText)))));

    private static string MissionProgressFingerprint(TranscriptEvent progress) => string.Join(
        "\u001f",
        new[]
        {
            NormalizeComparison(progress.MissionInternalName),
            NormalizeComparison(progress.MissionTitle),
            progress.MissionState.ToString(),
        }.Concat(progress.MissionObjectives
            .Where(objective => objective.VisibleInTracker)
            .OrderBy(objective => objective.TranscriptOrder)
            .Select(objective => string.Join(
                "\u001d",
                objective.InternalName,
                NormalizeComparison(objective.DisplayText),
                objective.State,
                objective.Count,
                objective.Target,
                objective.TranscriptOrder))));

    private static string InferMissionTitle(IReadOnlyList<TranscriptEvent> events)
    {
        foreach (var item in events.Where(item => item.Event == "missionMessage"))
        {
            var match = AcquiredMissionPattern().Match(item.Text);
            if (match.Success)
            {
                return match.Groups[1].Value.Trim().Trim('"');
            }
        }

        return events
            .Where(item => item.Event == "dialogue")
            .Select(item => item.DialogHeader)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "Unknown Mission";
    }

    internal static string CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = BreakPattern().Replace(value, "\n\n");
        cleaned = ClosingBlockPattern().Replace(cleaned, "\n\n");
        cleaned = TagPattern().Replace(cleaned, string.Empty);
        cleaned = WebUtility.HtmlDecode(cleaned).Replace("\u00a0", " ");
        cleaned = cleaned.Replace("\r\n", "\n").Replace('\r', '\n');
        cleaned = TrailingWhitespacePattern().Replace(cleaned, string.Empty);
        cleaned = ExcessBlankLinesPattern().Replace(cleaned, "\n\n");
        return cleaned.Trim();
    }

    private static string NormalizeComparison(string value) =>
        ComparisonWhitespacePattern().Replace(CleanText(value).ToLowerInvariant(), " ").Trim();

    private static string EscapeInline(string value) =>
        value.Replace("\\", "\\\\").Replace("*", "\\*").Replace("_", "\\_").Replace("`", "\\`");

    private static string FirstNonempty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "(unnamed choice)";

    private static bool HasVisibleOptionText(TranscriptOption option) =>
        !string.IsNullOrWhiteSpace(option.DisplayString) ||
        !string.IsNullOrWhiteSpace(option.DisplayString2) ||
        !string.IsNullOrWhiteSpace(option.ConfirmHeader) ||
        !string.IsNullOrWhiteSpace(option.ConfirmText) ||
        !string.IsNullOrWhiteSpace(option.InteractionDisabledMessage);

    private static DateTimeOffset? ParseTimestamp(string value) =>
        DateTimeOffset.TryParse(value, out var timestamp) ? timestamp : null;

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;

    private static bool GetBoolean(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        property.GetBoolean();

    private static int GetInt32(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;

    [GeneratedRegex(@"(?i)<br\s*/?>")]
    private static partial Regex BreakPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"(?i)</(?:p|div)\s*>")]
    private static partial Regex ClosingBlockPattern();

    [GeneratedRegex(@"[ \t]+$", RegexOptions.Multiline)]
    private static partial Regex TrailingWhitespacePattern();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLinesPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex ComparisonWhitespacePattern();

    [GeneratedRegex(@"(?i)(offscreenicon|skinpose_|defs/|\.wtex\b|\.cgeo\b)")]
    private static partial Regex TechnicalResourcePattern();

    [GeneratedRegex(@"(?i)^acquired mission:\s*(.+)$")]
    private static partial Regex AcquiredMissionPattern();
}
