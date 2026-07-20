namespace StoDialogueCapture;

internal sealed record ContactDialogOptionSnapshot(
    string Key,
    string DisplayString,
    string DisplayString2,
    int Type,
    bool NeedsConfirm,
    string ConfirmHeader,
    string ConfirmText,
    bool Recommended,
    int OptionIndex,
    bool ShowRewardChooser,
    bool IsDefaultBackOption,
    bool IsRefreshOption,
    bool Visited,
    bool CannotChoose,
    bool InteractionDisabled,
    string InteractionDisabledMessage);

internal sealed record ContactDialogSnapshot(
    DateTimeOffset CapturedAt,
    string Event,
    string Process,
    string Address,
    int ScreenType,
    string ContactDisplayName,
    string DialogHeader,
    string DialogText1,
    string DialogHeader2,
    string DialogText2,
    string ListHeader,
    string VoicePath,
    string PhrasePath,
    string LastResponseDisplayString,
    IReadOnlyList<ContactDialogOptionSnapshot> Options);

internal sealed record DialogueTransitionSnapshot(
    DateTimeOffset CapturedAt,
    string Event,
    string Process,
    string FromNodeId,
    string FromSpeaker,
    string SelectedChoice,
    string ToNodeId,
    string ToSpeaker);

internal sealed record DialogCandidate(
    ulong Address,
    int Score,
    ContactDialogSnapshot Snapshot);

internal sealed record DiscoveryResult(
    DialogCandidate Candidate,
    IReadOnlyList<ulong> PointerSlots,
    IReadOnlyList<ulong> AnchorAddresses);

internal sealed record TranscriptChatMessage(
    DateTimeOffset CapturedAt,
    string Event,
    string Process,
    string Channel,
    string? Speaker,
    string Text,
    string RawLine);

internal sealed record MissionObjectiveSnapshot(
    string InternalName,
    string DisplayText,
    string RawDisplayText,
    int State,
    string StateName,
    int Count,
    int Target,
    int Depth,
    int DisplayOrder,
    bool Tracking,
    bool Hidden,
    bool OmitFromTracker,
    bool VisibleInTracker,
    int MissionUiType,
    string IconName,
    IReadOnlyList<int> ObjectiveTags,
    string MarkerStyle,
    string MarkerText,
    string? RecruitmentType,
    string ParentInternalName,
    int TraversalOrder);

internal sealed record MissionProgressSnapshot(
    DateTimeOffset CapturedAt,
    string Event,
    string Process,
    string Address,
    string MissionInternalName,
    string MissionTitle,
    int State,
    string StateName,
    IReadOnlyList<MissionObjectiveSnapshot> Objectives);

internal sealed record CaptureCounters(
    int Dialogues,
    int Transitions,
    int NpcLines,
    int MissionMessages,
    int ProgressSnapshots);

internal sealed record CaptureActivity(
    string Kind,
    string? Speaker,
    string? Excerpt,
    IReadOnlyList<string>? Choices,
    string? SelectedChoice,
    string? ArrivedVia,
    CaptureCounters Counters);
