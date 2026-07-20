using System.Buffers.Binary;
using System.Text;

namespace StoDialogueCapture;

internal static class SelfTests
{
    public static void Run()
    {
        const ulong baseAddress = 0x100000;
        var memory = new SyntheticMemory(baseAddress, 0x50000, baseAddress, 0x10000);

        const ulong pointerSlot = 0x101000;
        const ulong dialog = 0x110000;
        const ulong contactDisplayName = 0x120000;
        const ulong header = 0x120080;
        const ulong text1 = 0x120100;
        const ulong text2 = 0x120300;
        const ulong optionArray = 0x130100;
        const ulong option = 0x131000;
        const ulong key = 0x132000;
        const ulong display = 0x132100;
        const ulong replacementDialog = 0x140000;
        const ulong replacementName = 0x142000;
        const ulong replacementText = 0x143000;
        const ulong lastResponse = 0x143200;
        const ulong falseCandidate = 0x144000;
        const ulong falseHeader = 0x145000;
        const ulong falseHeader2 = 0x145100;
        const ulong falseListHeader = 0x145200;

        memory.WriteUInt64(pointerSlot, dialog);
        memory.WriteInt32(dialog, 3);
        memory.WriteUInt64(dialog + 0x08, contactDisplayName);
        memory.WriteUInt64(dialog + 0x10, header);
        memory.WriteUInt64(dialog + 0x18, text1);
        memory.WriteUInt64(dialog + 0x30, text2);
        memory.WriteUInt64(dialog + 0xD8, optionArray);
        memory.WriteCString(contactDisplayName, "Mission Simulator");
        memory.WriteCString(header, "The Helix");
        memory.WriteCString(text1, "If we're going to have a chance, we need allies.");
        memory.WriteCString(text2, "There are settlements like yours out there.");
        memory.WriteInt32(optionArray - 0x10, 1);
        memory.WriteUInt64(optionArray, option);
        memory.WriteUInt64(option, key);
        memory.WriteUInt64(option + 0x08, display);
        memory.WriteByte(option + 0x8C, 1);
        memory.WriteInt32(option + 0x94, 7);
        memory.WriteByte(option + 0xA0, 1);
        memory.WriteByte(option + 0xAC, 1);
        memory.WriteCString(key, "Mission.Accept");
        memory.WriteCString(display, "Accept");
        memory.WriteInt32(replacementDialog, 2);
        memory.WriteUInt64(replacementDialog + 0x08, replacementName);
        memory.WriteUInt64(replacementDialog + 0x18, replacementText);
        memory.WriteUInt64(replacementDialog + 0xD8, optionArray);
        memory.WriteUInt64(replacementDialog + 0x200, lastResponse);
        memory.WriteCString(replacementName, "Tovan Khev");
        memory.WriteCString(
            replacementText,
            "Neral, we've arrived at the Helix but we're not the only ship here. The rest of the full dialogue follows.");
        memory.WriteCString(lastResponse, "Accept");
        memory.WriteInt32(falseCandidate, 0);
        memory.WriteUInt64(falseCandidate + 0x08, replacementText);
        memory.WriteUInt64(falseCandidate + 0x10, falseHeader);
        memory.WriteUInt64(falseCandidate + 0x28, falseHeader2);
        memory.WriteUInt64(falseCandidate + 0x38, falseListHeader);
        memory.WriteCString(falseHeader, "Rewardrow");
        memory.WriteCString(falseHeader2, "Rewardrowwithvalue");
        memory.WriteCString(falseListHeader, "{ItemName} {ItemValue}");

        using (memory)
        {
            var reader = new ContactDialogReader(memory);
            var result = reader.Discover("Mission Simulator");
            Assert(
                result.Candidate.Address == dialog,
                $"dialog address (expected 0x{dialog:X}, got 0x{result.Candidate.Address:X})");
            Assert(result.Candidate.Snapshot.ContactDisplayName == "Mission Simulator", "anchor field preference");
            Assert(result.Candidate.Snapshot.DialogHeader == "The Helix", "dialog header");
            Assert(result.PointerSlots.Contains(pointerSlot), "main-module pointer slot");
            Assert(result.Candidate.Snapshot.DialogText1.Contains("need allies"), "dialog text 1");
            Assert(result.Candidate.Snapshot.Options.Count == 1, "option count");
            Assert(result.Candidate.Snapshot.Options[0].Key == "Mission.Accept", "option key");
            Assert(result.Candidate.Snapshot.Options[0].DisplayString == "Accept", "option display");
            Assert(result.Candidate.Snapshot.Options[0].Recommended, "recommended flag");
            Assert(result.Candidate.Snapshot.Options[0].OptionIndex == 7, "option index");
            Assert(result.Candidate.Snapshot.Options[0].ShowRewardChooser, "reward chooser flag");
            Assert(result.Candidate.Snapshot.Options[0].IsDefaultBackOption, "default-back flag");

            var speakerTrigger = TranscriptChatLogTailer.ParseTranscriptLine(
                "[837801643,20260719T144043,0,Tovan Khev@,@,,,NPC]Neral, we've arrived at the Helix but we're not the only ship here.");
            Assert(speakerTrigger != null, "speaker reacquisition trigger");
            var speakerRecovery = reader.TryReacquire(speakerTrigger!);
            Assert(speakerRecovery != null, "speaker reacquisition");
            Assert(speakerRecovery!.Candidate.Address == replacementDialog, "speaker replacement address");
            Assert(
                speakerRecovery.Candidate.Snapshot.LastResponseDisplayString == "Accept",
                "last response display string");
            var transition = CaptureSession.TryCreateTransition(
                result.Candidate.Snapshot,
                speakerRecovery.Candidate.Snapshot,
                "GameClient");
            Assert(transition != null, "dialogue transition creation");
            Assert(transition!.SelectedChoice == "Accept", "dialogue transition selected choice");
            Assert(transition.FromNodeId != transition.ToNodeId, "dialogue transition node IDs");
            Assert(
                CaptureSession.TryCreateTransition(
                    result.Candidate.Snapshot,
                    speakerRecovery.Candidate.Snapshot with { LastResponseDisplayString = "Not offered" },
                    "GameClient") == null,
                "reject unmatched last response");

            var prefixTrigger = new TranscriptChatMessage(
                DateTimeOffset.UtcNow,
                "npcMessage",
                "GameClient",
                "NPC",
                null,
                "Neral, we've arrived at the Helix but we're not the only ship here.",
                "synthetic prefix trigger");
            var prefixRecovery = reader.TryReacquire(prefixTrigger);
            Assert(prefixRecovery != null, "prefix reacquisition");
            Assert(prefixRecovery!.Candidate.Address == replacementDialog, "prefix replacement address");
            Assert(
                reader.TryReadBest([falseCandidate], requireLiveDialogue: true) == null,
                "reject zero-option text-container false positive");
        }

        var missionLine = TranscriptChatLogTailer.ParseTranscriptLine(
            "[837801190,20260719T143310,0,@,@,,,Mission]One more to go. As soon as Delta is connected, we can negotiate a transfer.");
        Assert(missionLine != null, "mission chat parsing");
        Assert(
            missionLine!.Text == "One more to go. As soon as Delta is connected, we can negotiate a transfer.",
            "mission chat text");
        Assert(
            TranscriptChatLogTailer.ParseTranscriptLine(
                "[837801189,20260719T143309,0,@,@,,,System]ChatLog 1") == null,
            "non-mission chat filtering");

        var npcLine = TranscriptChatLogTailer.ParseTranscriptLine(
            "[837801191,20260719T143311,0,@,@,,,NPC]Yridian Colonist: What does the Romulan Republic want with us?");
        Assert(npcLine != null, "NPC chat parsing");
        Assert(npcLine!.Event == "npcMessage", "NPC event type");
        Assert(npcLine.Channel == "NPC", "NPC channel");
        Assert(npcLine.Speaker == "Yridian Colonist", "NPC speaker");
        Assert(npcLine.Text == "What does the Romulan Republic want with us?", "NPC text");

        var metadataSpeaker = TranscriptChatLogTailer.ParseTranscriptLine(
            "[837801643,20260719T144043,0,Tovan Khev@,@,,,NPC]Neral, we've arrived at the Helix but we're not the only ship here.");
        Assert(metadataSpeaker != null, "metadata NPC parsing");
        Assert(metadataSpeaker!.Speaker == "Tovan Khev", "metadata NPC speaker");

        TestChatLogContinuationTailer();
        TestTranscriptRenderer();
        TestTranscriptMissionProgressCorrections();
    }

    private static void TestChatLogContinuationTailer()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"sto-chat-continuation-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            var logPath = Path.Combine(testDirectory, "chat_test.log");
            File.WriteAllText(logPath, string.Empty, new UTF8Encoding(false));
            var tailer = TranscriptChatLogTailer.Create(logPath, "GameClient");
            File.AppendAllText(
                logPath,
                "[837828553,20260719T220913,0,@,@,,,Mission]Look up! \r\n" +
                "We've got to get out of here NOW!\r\n",
                new UTF8Encoding(false));
            Assert(tailer.ReadNewMessages().Count == 0, "hold newest logical chat record for continuation");
            var missionMessages = tailer.ReadNewMessages();
            Assert(missionMessages.Count == 1, "flush logical chat record after quiet poll");
            Assert(
                missionMessages[0].Text == "Look up!\nWe've got to get out of here NOW!",
                "mission continuation joining");
            Assert(
                missionMessages[0].RawLine.Contains("\nWe've got to get out of here NOW!"),
                "combined continuation raw record");

            File.AppendAllText(
                logPath,
                "[837828551,20260719T220911,0,Veril@,@,,,NPC]We should go back.\r\n\r\n" +
                "Return to Zden.\r\n" +
                "[837828562,20260719T220922,0,@,@,,,System]Ignored\r\n",
                new UTF8Encoding(false));
            var npcMessages = tailer.ReadNewMessages();
            Assert(npcMessages.Count == 1, "flush continuation at next structured header");
            Assert(
                npcMessages[0].Text == "We should go back.\n\nReturn to Zden.",
                "NPC blank-line continuation joining");
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static void TestTranscriptRenderer()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"sto-dialogue-transcript-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            var jsonlPath = Path.Combine(testDirectory, "capture.jsonl");
            var transcriptPath = Path.Combine(testDirectory, "capture.transcript.md");
            var lines = new[]
            {
                """{"capturedAt":"2026-07-19T15:00:00-04:00","event":"dialogue","screenType":2,"contactDisplayName":"Tovan Khev","dialogHeader":"","dialogText1":"First sentence.<br><br>\n\nFull second sentence.","dialogHeader2":"","dialogText2":"","listHeader":"","options":[{"displayString":"Continue","displayString2":"","needsConfirm":false,"confirmHeader":"","confirmText":"","interactionDisabled":false,"interactionDisabledMessage":""}]}""",
                """{"capturedAt":"2026-07-19T15:00:01-04:00","event":"npcMessage","speaker":"Tovan Khev","text":"First sentence."}""",
                """{"capturedAt":"2026-07-19T15:00:02-04:00","event":"dialogue","screenType":2,"contactDisplayName":"Tovan Khev","dialogHeader":"","dialogText1":"First sentence.<br><br>\n\nFull second sentence.","dialogHeader2":"","dialogText2":"","listHeader":"","options":[{"displayString":"Continue","displayString2":"","needsConfirm":false,"confirmHeader":"","confirmText":"","interactionDisabled":false,"interactionDisabledMessage":""}]}""",
                """{"capturedAt":"2026-07-19T15:00:02.200-04:00","event":"dialogue","screenType":2,"contactDisplayName":"Tovan Khev","dialogHeader":"","dialogText1":"Second node.","dialogHeader2":"","dialogText2":"","listHeader":"","lastResponseDisplayString":"Continue","options":[{"displayString":"Back","displayString2":"","needsConfirm":false,"confirmHeader":"","confirmText":"","interactionDisabled":false,"interactionDisabledMessage":""}]}""",
                """{"capturedAt":"2026-07-19T15:00:02.400-04:00","event":"dialogue","screenType":2,"contactDisplayName":"Tovan Khev","dialogHeader":"","dialogText1":"First sentence.<br><br>\n\nFull second sentence.","dialogHeader2":"","dialogText2":"","listHeader":"","lastResponseDisplayString":"Back","options":[{"displayString":"Continue","displayString2":"","needsConfirm":false,"confirmHeader":"","confirmText":"","interactionDisabled":false,"interactionDisabledMessage":""}]}""",
                """{"capturedAt":"2026-07-19T15:00:02.600-04:00","event":"dialogue","screenType":2,"contactDisplayName":"Tovan Khev","dialogHeader":"","dialogText1":"Third node.","dialogHeader2":"","dialogText2":"","listHeader":"","lastResponseDisplayString":"Continue","options":[{"displayString":"Done","displayString2":"","needsConfirm":false,"confirmHeader":"","confirmText":"","interactionDisabled":false,"interactionDisabledMessage":""}]}""",
                """{"capturedAt":"2026-07-19T15:00:03-04:00","event":"npcMessage","speaker":"Colonist","text":"Ambient line."}""",
                """{"capturedAt":"2026-07-19T15:00:04-04:00","event":"missionMessage","speaker":null,"text":"Acquired mission: The Helix"}""",
                """{"capturedAt":"2026-07-19T15:00:04.500-04:00","event":"missionProgress","missionTitle":"Runaway Helix","state":0,"stateName":"inProgress","objectives":[{"internalName":"Read_Message","displayText":"Answer Incoming Hail","state":1,"stateName":"succeeded","count":0,"target":0,"depth":1,"displayOrder":1,"visibleInTracker":true},{"internalName":"Defeat_Sharrdar","displayText":"Defeat the I.R.W. Sharrdar","state":0,"stateName":"inProgress","count":0,"target":0,"depth":1,"displayOrder":2,"visibleInTracker":true},{"internalName":"Invis_Dr_Pause","displayText":"Invis_Dr_Pause","state":1,"stateName":"succeeded","count":0,"target":0,"depth":1,"displayOrder":3,"visibleInTracker":false}]}""",
                """{"capturedAt":"2026-07-19T15:00:04.750-04:00","event":"missionProgress","missionTitle":"Runaway Helix","state":0,"stateName":"inProgress","objectives":[{"internalName":"Read_Message","displayText":"Answer Incoming Hail","state":1,"stateName":"succeeded","count":0,"target":0,"depth":1,"displayOrder":1,"visibleInTracker":true},{"internalName":"Defeat_Sharrdar","displayText":"Defeat the I.R.W. Sharrdar","state":0,"stateName":"inProgress","count":0,"target":0,"depth":1,"displayOrder":2,"visibleInTracker":true},{"internalName":"Invis_Dr_Pause","displayText":"Invis_Dr_Pause","state":0,"stateName":"inProgress","count":1,"target":1,"depth":1,"displayOrder":3,"visibleInTracker":false}]}""",
                """{"capturedAt":"2026-07-19T15:00:05-04:00","event":"dialogue","screenType":0,"contactDisplayName":"","dialogHeader":"Offscreenicon","dialogText1":"Defs/Bad.Wtex","dialogHeader2":"","dialogText2":"","listHeader":"","options":[]}""",
                """{"capturedAt":"2026-07-19T15:00:06-04:00","event":"npcMessage","speaker":"Satra","text":"Recovered first sentence."}""",
                """{"capturedAt":"2026-07-19T15:00:07-04:00","event":"dialogue","screenType":0,"contactDisplayName":"Recovered first sentence.<br><br>Recovered full second sentence.","dialogHeader":"Rewardrow","dialogText1":"","dialogHeader2":"Rewardrowwithvalue","dialogText2":"","listHeader":"{ItemName}","options":[]}""",
            };
            File.WriteAllLines(jsonlPath, lines, new UTF8Encoding(false));

            var result = TranscriptRenderer.Render(jsonlPath, transcriptPath);
            var markdown = File.ReadAllText(transcriptPath);
            Assert(result.DialogueCount == 5, "transcript route-aware deduplication and recovery");
            Assert(result.MissionMessageCount == 1, "transcript mission inclusion");
            Assert(result.NpcMessageCount == 1, "transcript NPC overlap removal");
            Assert(result.MissionProgressCount == 1, "transcript visible mission-progress deduplication");
            Assert(result.SuppressedCount == 5, "transcript cleanup count");
            Assert(markdown.Contains("# Mission Transcript: The Helix"), "transcript title inference");
            Assert(markdown.Contains("Full second sentence."), "transcript full dialogue");
            Assert(markdown.Contains("- Continue"), "transcript choice");
            Assert(markdown.Contains("- Continue **(selected)**"), "transcript selected choice marker");
            Assert(markdown.Contains("**Arrived via choice:** Continue"), "transcript inbound choice");
            Assert(markdown.Contains("**Arrived via choice:** Back"), "transcript back transition");
            Assert(
                markdown.Split("Full second sentence.").Length - 1 == 2,
                "transcript preserves meaningful revisit");
            Assert(markdown.Contains("Ambient line."), "transcript ambient NPC line");
            Assert(markdown.Contains("## Mission progress - Runaway Helix"), "transcript mission progress heading");
            Assert(markdown.Contains("- [x] Answer Incoming Hail"), "transcript completed objective");
            Assert(markdown.Contains("- [ ] Defeat the I.R.W. Sharrdar"), "transcript active objective");
            Assert(!markdown.Contains("Invis_Dr_Pause"), "transcript hidden control objective filtering");
            Assert(markdown.Contains("## Satra"), "transcript recovered speaker");
            Assert(markdown.Contains("Recovered full second sentence."), "transcript recovered body");
            Assert(!markdown.Contains("Offscreenicon"), "transcript invalid candidate removal");
            Assert(
                TranscriptRenderer.CleanText("<p><font Style=game_Header>The Helix</font></p>Body") ==
                "The Helix\n\nBody",
                "transcript block-tag spacing");
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static void TestTranscriptMissionProgressCorrections()
    {
        var testDirectory = Path.Combine(
            Path.GetTempPath(),
            $"sto-mission-progress-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);
        try
        {
            var jsonlPath = Path.Combine(testDirectory, "capture.jsonl");
            var transcriptPath = Path.Combine(testDirectory, "capture.transcript.md");
            var lines = new[]
            {
                """{"capturedAt":"2026-07-19T15:00:00.500-04:00","event":"missionProgress","missionInternalName":"Rom_Test_Pm","missionTitle":"The Helix","state":0,"stateName":"inProgress","objectives":[{"internalName":"Part_1","displayText":"Runaway Helix","state":0,"stateName":"inProgress","count":0,"target":0,"depth":1,"displayOrder":1,"visibleInTracker":true}]}""",
                """{"capturedAt":"2026-07-19T15:00:01-04:00","event":"missionProgress","missionInternalName":"Rom_Test_Om","missionTitle":"Runaway Helix","state":0,"stateName":"inProgress","objectives":[{"internalName":"Assist","displayText":"Negotiate Transfer","state":0,"stateName":"inProgress","count":0,"target":0,"depth":1,"displayOrder":13,"visibleInTracker":true}]}""",
                """{"capturedAt":"2026-07-19T15:00:00-04:00","event":"missionMessage","speaker":null,"text":"Acquired mission: The Helix"}""",
                """{"capturedAt":"2026-07-19T15:00:02-04:00","event":"missionProgress","missionInternalName":"Rom_Test_Om","missionTitle":"Runaway Helix","state":0,"stateName":"inProgress","objectives":[{"internalName":"Assist","displayText":"Negotiate Transfer","state":0,"stateName":"inProgress","count":0,"target":0,"depth":1,"displayOrder":13,"visibleInTracker":true},{"internalName":"Repair_Substation","displayText":"Beam up Refugees","state":0,"stateName":"inProgress","count":0,"target":0,"depth":2,"displayOrder":802,"visibleInTracker":true}]}""",
                """{"capturedAt":"2026-07-19T15:00:03-04:00","event":"missionProgress","missionInternalName":"Rom_Test_Om","missionTitle":"Runaway Helix","state":0,"stateName":"inProgress","objectives":[{"internalName":"Assist","displayText":"Negotiate Transfer","state":1,"stateName":"succeeded","count":0,"target":0,"depth":1,"displayOrder":13,"visibleInTracker":true},{"internalName":"Defeat_Koval","displayText":"Defeat the I.R.W. Koval","state":0,"stateName":"inProgress","count":0,"target":0,"depth":1,"displayOrder":14,"visibleInTracker":true},{"internalName":"Repair_Substation","displayText":"Beam up Refugees","state":1,"stateName":"succeeded","count":0,"target":0,"depth":2,"displayOrder":802,"visibleInTracker":true}]}""",
                """{"capturedAt":"2026-07-19T15:00:03.500-04:00","event":"missionProgress","missionInternalName":"Rom_Test_Pm","missionTitle":"Runaway Helix","state":0,"stateName":"inProgress","objectives":[{"internalName":"Part_1","displayText":"Runaway Helix","state":0,"stateName":"inProgress","count":0,"target":0,"depth":1,"displayOrder":1,"visibleInTracker":true}]}""",
                """{"capturedAt":"2026-07-19T15:00:04-04:00","event":"missionProgress","missionInternalName":"Rom_Test_Pm","missionTitle":"Runaway Helix","state":0,"stateName":"inProgress","objectives":[{"internalName":"Part_2","displayText":"Return to Sector Space","state":0,"stateName":"inProgress","count":0,"target":0,"depth":1,"displayOrder":2,"visibleInTracker":true}]}""",
                """{"capturedAt":"2026-07-19T15:00:05-04:00","event":"missionProgress","missionInternalName":"Rom_Test_Om","missionTitle":"Runaway Helix","state":1,"stateName":"succeeded","objectives":[{"internalName":"Defeat_Koval","displayText":"Defeat the I.R.W. Koval","state":1,"stateName":"succeeded","count":0,"target":0,"depth":1,"displayOrder":14,"visibleInTracker":true}]}""",
                """{"capturedAt":"2026-07-19T15:00:06-04:00","event":"dialogue","screenType":2,"contactDisplayName":"First Speaker","dialogHeader":"","dialogText1":"First node.","dialogHeader2":"","dialogText2":"","listHeader":"","options":[{"displayString":"Proceed","displayString2":"","needsConfirm":false,"confirmHeader":"","confirmText":"","interactionDisabled":false,"interactionDisabledMessage":""}]}""",
                """{"capturedAt":"2026-07-19T15:00:07-04:00","event":"dialogue","screenType":2,"contactDisplayName":"Second Speaker","dialogHeader":"","dialogText1":"Second node.","dialogHeader2":"","dialogText2":"","listHeader":"","options":[{"displayString":"Done","displayString2":"","needsConfirm":false,"confirmHeader":"","confirmText":"","interactionDisabled":false,"interactionDisabledMessage":""}]}""",
                """{"capturedAt":"2026-07-19T15:00:08-04:00","event":"missionMessage","speaker":null,"text":"Look up!"}""",
                """{"capturedAt":"2026-07-19T15:00:08-04:00","event":"missionMessage","speaker":null,"text":"Look up!\nWe've got to get out of here NOW!"}""",
            };
            File.WriteAllLines(jsonlPath, lines, new UTF8Encoding(false));

            var result = TranscriptRenderer.Render(jsonlPath, transcriptPath);
            var markdown = File.ReadAllText(transcriptPath);
            Assert(result.MissionProgressCount == 4, "stale OpenMission suppression after wrapper activation");
            Assert(!markdown.Contains("## Mission progress - The Helix"), "pre-OpenMission section gate suppression");
            Assert(!markdown.Contains("- [ ] Runaway Helix"), "section-title echo suppression");
            Assert(
                markdown.IndexOf("Acquired mission: The Helix", StringComparison.Ordinal) <
                markdown.IndexOf("## Mission progress", StringComparison.Ordinal),
                "transcript timestamp chronology");
            Assert(
                markdown.IndexOf("- [x] Beam up Refugees", StringComparison.Ordinal) <
                markdown.IndexOf("- [ ] Defeat the I.R.W. Koval", StringComparison.Ordinal),
                "first-seen objective ordering across mission-tree depths");
            Assert(!markdown.Contains("\n  - ["), "mission graph depth is not rendered as HUD indentation");
            Assert(!markdown.Contains("- [x] Defeat the I.R.W. Koval"), "stale completed checklist omitted");
            Assert(markdown.Contains("- Proceed **(selected)**"), "single enabled choice selected");
            Assert(markdown.Contains("**Arrived via choice:** Proceed"), "nearby single-choice route linked");
            Assert(markdown.Contains("We've got to get out of here NOW!"), "longer logical chat record retained");
            Assert(
                markdown.Split("Look up!").Length - 1 == 1,
                "short chat prefix suppressed when corrected logical record exists");
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static void Assert(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Self-test failed: {name}");
        }
    }
}

internal sealed class SyntheticMemory : IMemoryReader
{
    private readonly ulong _baseAddress;
    private readonly byte[] _data;

    public ulong MainModuleBase { get; }
    public ulong MainModuleSize { get; }
    public IReadOnlyList<MemoryRegion> Regions { get; }

    public SyntheticMemory(ulong baseAddress, int size, ulong moduleBase, ulong moduleSize)
    {
        _baseAddress = baseAddress;
        _data = new byte[size];
        MainModuleBase = moduleBase;
        MainModuleSize = moduleSize;
        Regions = [new MemoryRegion(baseAddress, (ulong)size, 0x1000, 0x04, 0x20000)];
    }

    public bool TryRead(ulong address, byte[] buffer, int offset, int count, out int bytesRead)
    {
        if (address < _baseAddress || address + (ulong)count > _baseAddress + (ulong)_data.Length)
        {
            bytesRead = 0;
            return false;
        }
        Buffer.BlockCopy(_data, checked((int)(address - _baseAddress)), buffer, offset, count);
        bytesRead = count;
        return true;
    }

    public void RefreshRegions()
    {
    }

    public void WriteUInt64(ulong address, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(Slice(address, 8), value);

    public void WriteInt32(ulong address, int value) =>
        BinaryPrimitives.WriteInt32LittleEndian(Slice(address, 4), value);

    public void WriteByte(ulong address, byte value) =>
        Slice(address, 1)[0] = value;

    public void WriteCString(ulong address, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        bytes.CopyTo(Slice(address, bytes.Length));
    }

    private Span<byte> Slice(ulong address, int length) =>
        _data.AsSpan(checked((int)(address - _baseAddress)), length);

    public void Dispose()
    {
    }
}
