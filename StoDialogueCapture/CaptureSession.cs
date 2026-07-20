using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StoDialogueCapture;

internal enum CaptureState
{
    Starting,
    Scanning,
    Capturing,
    Waiting,
    Rendering,
    Completed,
}

internal sealed record CaptureProgress(CaptureState State, string Message);

internal sealed record CaptureRunResult(
    string JsonlPath,
    TranscriptRenderResult? Transcript);

internal static class CaptureSession
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static async Task<CaptureRunResult> RunAsync(
        CaptureOptions options,
        Action<CaptureProgress>? progress,
        CancellationToken cancellationToken)
    {
        void Report(CaptureState state, string message) =>
            progress?.Invoke(new CaptureProgress(state, message));

        Report(CaptureState.Starting, $"Connecting read-only to {options.ProcessName}...");
        using var memory = ProcessMemory.Open(options.ProcessName);
        Report(
            CaptureState.Starting,
            $"Attached read-only to {options.ProcessName}; module " +
            $"0x{memory.MainModuleBase:X}-0x{memory.MainModuleBase + memory.MainModuleSize:X}.");

        TranscriptChatLogTailer? transcriptChatLog = null;
        if (options.CaptureMissionChat)
        {
            transcriptChatLog = options.ChatLogPath == null
                ? TranscriptChatLogTailer.CreateAuto(
                    memory.ProcessPath,
                    options.ProcessName,
                    message => Report(CaptureState.Starting, message))
                : TranscriptChatLogTailer.Create(
                    options.ChatLogPath,
                    options.ProcessName,
                    message => Report(CaptureState.Starting, message));
        }

        var reader = new ContactDialogReader(memory);
        var missionProgressReader = new MissionProgressReader(memory);
        Report(CaptureState.Scanning, $"Locating the open '{options.Anchor}' dialogue...");
        var discovery = reader.Discover(
            options.Anchor!,
            message => Report(CaptureState.Scanning, message));

        var fullOutputPath = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        await using var output = new StreamWriter(
            new FileStream(fullOutputPath, FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        Report(CaptureState.Capturing, $"Writing changed dialogue nodes to {fullOutputPath}");

        var directAddress = discovery.Candidate.Address;
        var pointerSlots = discovery.PointerSlots;
        string? lastFingerprint = null;
        string? lastMissionProgressFingerprint = null;
        var wasOpen = false;
        var waitingForReacquisition = false;
        string? closedFingerprint = null;
        TranscriptChatMessage? latestNpcTrigger = null;
        ContactDialogSnapshot? previousDialogue = null;
        var attemptedReacquisitionLines = new HashSet<string>(StringComparer.Ordinal);
        var nextReacquisitionAttempt = DateTimeOffset.MinValue;

        async Task RecordChatMessage(TranscriptChatMessage chatMessage)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(chatMessage, JsonOptions));
            var speaker = string.IsNullOrWhiteSpace(chatMessage.Speaker)
                ? string.Empty
                : $" {chatMessage.Speaker}:";
            Report(
                CaptureState.Capturing,
                $"[{DateTime.Now:T}] [{chatMessage.Channel}]{speaker} {chatMessage.Text}");
            if (chatMessage.Event == "npcMessage")
            {
                latestNpcTrigger = chatMessage;
            }
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (transcriptChatLog != null)
                {
                    foreach (var chatMessage in transcriptChatLog.ReadNewMessages(
                                 message => Report(CaptureState.Capturing, message)))
                    {
                        await RecordChatMessage(chatMessage);
                    }
                }

                DialogCandidate? candidate = null;
                if (!waitingForReacquisition)
                {
                    var addresses = new List<ulong> { directAddress };
                    addresses.AddRange(reader.ResolvePointerSlots(pointerSlots));
                    candidate = reader.TryReadBest(addresses);
                }
                else
                {
                    var replacement = reader.TryReadBest(
                        reader.ResolvePointerSlots(pointerSlots),
                        requireLiveDialogue: true);
                    if (replacement != null)
                    {
                        var replacementFingerprint = Fingerprint(replacement.Snapshot);
                        if (!string.Equals(
                                replacementFingerprint,
                                closedFingerprint,
                                StringComparison.Ordinal))
                        {
                            candidate = replacement;
                            waitingForReacquisition = false;
                            closedFingerprint = null;
                            latestNpcTrigger = null;
                            var reacquired = new
                            {
                                capturedAt = DateTimeOffset.UtcNow,
                                @event = "reacquired",
                                process = options.ProcessName,
                                triggerChannel = "pointerSlot",
                                triggerSpeaker = (string?)null,
                                triggerText = (string?)null,
                                address = $"0x{replacement.Address:X}",
                                pointerSlotCount = pointerSlots.Count,
                            };
                            await output.WriteLineAsync(JsonSerializer.Serialize(reacquired, JsonOptions));
                            Report(
                                CaptureState.Capturing,
                                $"[{DateTime.Now:T}] followed pointer slot to live " +
                                $"{replacement.Snapshot.ContactDisplayName} at 0x{replacement.Address:X}");
                        }
                    }
                }

                if (candidate == null &&
                    waitingForReacquisition &&
                    latestNpcTrigger != null &&
                    !attemptedReacquisitionLines.Contains(latestNpcTrigger.RawLine) &&
                    DateTimeOffset.UtcNow >= nextReacquisitionAttempt)
                {
                    attemptedReacquisitionLines.Add(latestNpcTrigger.RawLine);
                    var trigger = latestNpcTrigger;
                    var recovered = reader.TryReacquire(
                        trigger,
                        message => Report(CaptureState.Scanning, message));
                    nextReacquisitionAttempt = DateTimeOffset.UtcNow.AddSeconds(5);
                    if (recovered != null)
                    {
                        directAddress = recovered.Candidate.Address;
                        pointerSlots = recovered.PointerSlots;
                        candidate = recovered.Candidate;
                        waitingForReacquisition = false;
                        closedFingerprint = null;
                        latestNpcTrigger = null;
                        var reacquired = new
                        {
                            capturedAt = DateTimeOffset.UtcNow,
                            @event = "reacquired",
                            process = options.ProcessName,
                            triggerChannel = trigger.Channel,
                            triggerSpeaker = trigger.Speaker,
                            triggerText = trigger.Text,
                            address = $"0x{directAddress:X}",
                            pointerSlotCount = pointerSlots.Count,
                        };
                        await output.WriteLineAsync(JsonSerializer.Serialize(reacquired, JsonOptions));
                        Report(
                            CaptureState.Capturing,
                            $"[{DateTime.Now:T}] reacquired {recovered.Candidate.Snapshot.ContactDisplayName} " +
                            $"at 0x{directAddress:X} ({pointerSlots.Count} pointer slot(s))");
                    }
                }

                if (candidate != null)
                {
                    directAddress = candidate.Address;
                    var snapshot = candidate.Snapshot with { CapturedAt = DateTimeOffset.UtcNow };
                    var fingerprint = Fingerprint(snapshot);
                    if (!string.Equals(fingerprint, lastFingerprint, StringComparison.Ordinal))
                    {
                        var transition = TryCreateTransition(previousDialogue, snapshot, options.ProcessName);
                        if (transition != null)
                        {
                            await output.WriteLineAsync(JsonSerializer.Serialize(transition, JsonOptions));
                            Report(
                                CaptureState.Capturing,
                                $"[{DateTime.Now:T}] selected '{transition.SelectedChoice}' -> " +
                                $"{transition.ToSpeaker}");
                        }
                        await output.WriteLineAsync(JsonSerializer.Serialize(snapshot, JsonOptions));
                        var title = string.IsNullOrWhiteSpace(snapshot.ContactDisplayName)
                            ? snapshot.DialogHeader
                            : snapshot.ContactDisplayName;
                        Report(
                            CaptureState.Capturing,
                            $"[{DateTime.Now:T}] {title} " +
                            $"({snapshot.Options.Count} option(s), {snapshot.Address})");
                        lastFingerprint = fingerprint;
                        previousDialogue = snapshot;
                    }
                    wasOpen = true;
                    waitingForReacquisition = false;
                }
                else if (wasOpen)
                {
                    var closed = new
                    {
                        capturedAt = DateTimeOffset.UtcNow,
                        @event = "closed",
                        process = options.ProcessName,
                    };
                    await output.WriteLineAsync(JsonSerializer.Serialize(closed, JsonOptions));
                    Report(CaptureState.Waiting, $"[{DateTime.Now:T}] dialogue closed");
                    wasOpen = false;
                    closedFingerprint = lastFingerprint;
                    lastFingerprint = null;
                    waitingForReacquisition = true;
                    latestNpcTrigger = null;
                    Report(
                        CaptureState.Waiting,
                        "Waiting for the next live dialogue or NPC speaker after the transition.");
                }

                try
                {
                    var missionProgress = missionProgressReader.TryRead(
                        options.ProcessName,
                        message => Report(CaptureState.Scanning, message));
                    if (missionProgress != null)
                    {
                        var missionFingerprint = Fingerprint(missionProgress);
                        if (!string.Equals(
                                missionFingerprint,
                                lastMissionProgressFingerprint,
                                StringComparison.Ordinal))
                        {
                            await output.WriteLineAsync(JsonSerializer.Serialize(missionProgress, JsonOptions));
                            var active = missionProgress.Objectives
                                .Where(objective => objective.VisibleInTracker && objective.State == 0)
                                .Select(objective => objective.DisplayText)
                                .ToList();
                            Report(
                                CaptureState.Capturing,
                                $"[{DateTime.Now:T}] Mission progress — {missionProgress.MissionTitle}: " +
                                (active.Count == 0 ? missionProgress.StateName : string.Join("; ", active)));
                            lastMissionProgressFingerprint = missionFingerprint;
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Report(CaptureState.Waiting, $"Mission progress read deferred: {exception.Message}");
                }

                await Task.Delay(options.PollMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stop/Cancel is the normal end of a capture session.
        }

        if (transcriptChatLog?.FlushPendingMessage() is { } finalChatMessage)
        {
            await RecordChatMessage(finalChatMessage);
        }

        await output.FlushAsync();
        TranscriptRenderResult? transcript = null;
        if (options.GenerateTranscript)
        {
            Report(CaptureState.Rendering, "Creating the human-readable transcript...");
            transcript = TranscriptRenderer.Render(options.OutputPath, options.TranscriptPath);
            Report(CaptureState.Completed, $"Transcript written to {transcript.OutputPath}");
        }
        else
        {
            Report(CaptureState.Completed, "Capture stopped; JSONL saved.");
        }

        return new CaptureRunResult(fullOutputPath, transcript);
    }

    private static string Fingerprint(ContactDialogSnapshot snapshot)
    {
        var stable = new
        {
            snapshot.ScreenType,
            snapshot.ContactDisplayName,
            snapshot.DialogHeader,
            snapshot.DialogText1,
            snapshot.DialogHeader2,
            snapshot.DialogText2,
            snapshot.ListHeader,
            snapshot.VoicePath,
            snapshot.PhrasePath,
            snapshot.Options,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(stable, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string Fingerprint(MissionProgressSnapshot snapshot)
    {
        var stable = new
        {
            snapshot.MissionInternalName,
            snapshot.MissionTitle,
            snapshot.State,
            objectives = snapshot.Objectives
                .Where(objective => objective.VisibleInTracker)
                .Select(objective => new
                {
                    objective.InternalName,
                    objective.DisplayText,
                    objective.State,
                    objective.Count,
                    objective.Target,
                    objective.Depth,
                    objective.DisplayOrder,
                    objective.MarkerStyle,
                    objective.MarkerText,
                    objective.RecruitmentType,
                }),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(stable, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    internal static DialogueTransitionSnapshot? TryCreateTransition(
        ContactDialogSnapshot? previous,
        ContactDialogSnapshot current,
        string processName)
    {
        if (previous == null || string.IsNullOrWhiteSpace(current.LastResponseDisplayString))
        {
            return null;
        }

        var selectedChoice = TranscriptRenderer.CleanText(current.LastResponseDisplayString);
        var matchesOfferedChoice = previous.Options.Any(option =>
            ChoiceEquals(option.DisplayString, selectedChoice) ||
            ChoiceEquals(option.DisplayString2, selectedChoice));
        if (!matchesOfferedChoice)
        {
            return null;
        }

        var fromNodeId = NodeId(previous);
        var toNodeId = NodeId(current);
        if (string.Equals(fromNodeId, toNodeId, StringComparison.Ordinal))
        {
            return null;
        }

        return new DialogueTransitionSnapshot(
            current.CapturedAt,
            "dialogueTransition",
            processName,
            fromNodeId,
            DialogueTitle(previous),
            selectedChoice,
            toNodeId,
            DialogueTitle(current));
    }

    private static bool ChoiceEquals(string offered, string selected) =>
        !string.IsNullOrWhiteSpace(offered) &&
        string.Equals(
            TranscriptRenderer.CleanText(offered),
            TranscriptRenderer.CleanText(selected),
            StringComparison.OrdinalIgnoreCase);

    private static string NodeId(ContactDialogSnapshot snapshot)
    {
        var stable = new
        {
            snapshot.ContactDisplayName,
            snapshot.DialogHeader,
            snapshot.DialogText1,
            snapshot.DialogHeader2,
            snapshot.DialogText2,
            snapshot.ListHeader,
            options = snapshot.Options.Select(option => new
            {
                option.Key,
                option.DisplayString,
                option.DisplayString2,
            }),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(stable, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(bytes))[..16];
    }

    private static string DialogueTitle(ContactDialogSnapshot snapshot) =>
        string.IsNullOrWhiteSpace(snapshot.ContactDisplayName)
            ? snapshot.DialogHeader
            : snapshot.ContactDisplayName;
}
