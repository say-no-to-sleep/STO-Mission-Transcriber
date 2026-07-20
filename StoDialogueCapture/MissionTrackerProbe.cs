using System.Buffers.Binary;
using System.Text;

namespace StoDialogueCapture;

internal static class MissionTrackerProbe
{
    private const int ScanBlockSize = 1024 * 1024;
    private const uint MemPrivate = 0x20000;
    private static readonly int[] JournalStringOffsets = [0x30, 0x38, 0x40, 0x48, 0x50, 0x58, 0x90, 0x98];

    internal static void RunPlayer(IMemoryReader memory, string playerName, Action<string> output)
    {
        memory.RefreshRegions();
        var regions = memory.Regions
            .Where(region => region.IsReadable)
            .OrderBy(region => region.BaseAddress)
            .ToList();
        var privateRegions = regions.Where(region => region.Type == MemPrivate).ToList();
        output($"Probing {privateRegions.Count:N0} readable private regions for EntityPlayer (read-only).");
        ProbePlayerMissionTree(memory, regions, privateRegions, playerName, output);
    }

    internal static void Run(
        IMemoryReader memory,
        string missionTitle,
        string objectiveText,
        string? playerName,
        Action<string> output)
    {
        memory.RefreshRegions();
        var regions = memory.Regions
            .Where(region => region.IsReadable)
            .OrderBy(region => region.BaseAddress)
            .ToList();
        var privateRegions = regions.Where(region => region.Type == MemPrivate).ToList();

        output($"Probing {privateRegions.Count:N0} readable private regions (read-only).");
        var anchors = new[] { missionTitle, objectiveText };
        var allCandidates = new Dictionary<ulong, MissionJournalRowProbe>();

        foreach (var anchor in anchors)
        {
            var stringAddresses = FindExactStringAddresses(memory, privateRegions, anchor, 512).Distinct().ToList();
            output($"'{anchor}': {stringAddresses.Count:N0} exact string address(es).");
            var references = FindPointerReferences(memory, privateRegions, stringAddresses.ToHashSet(), 8192).Distinct().ToList();
            output($"'{anchor}': {references.Count:N0} aligned pointer reference(s).");
            foreach (var reference in references.Take(8))
            {
                if (TryReadUInt64(memory, reference, out var target))
                {
                    output($"  TEXTREF 0x{reference:X} -> 0x{target:X}");
                    DumpPointerContext(memory, regions, privateRegions, reference, output);
                }
            }
            ProbeMessageAndMissionDefinitions(
                memory,
                regions,
                privateRegions,
                anchor,
                stringAddresses.ToHashSet(),
                references,
                output);

            foreach (var reference in references)
            {
                foreach (var offset in JournalStringOffsets)
                {
                    if (reference < (ulong)offset)
                    {
                        continue;
                    }
                    var address = reference - (ulong)offset;
                    if (TryReadJournalRow(memory, regions, address, anchor, out var candidate) &&
                        (!allCandidates.TryGetValue(address, out var existing) || candidate.Score > existing.Score))
                    {
                        allCandidates[address] = candidate;
                    }
                }
            }
        }

        var ordered = allCandidates.Values.OrderByDescending(candidate => candidate.Score).ToList();
        output($"Validated {ordered.Count:N0} MissionJournalRow candidate(s).");
        foreach (var candidate in ordered.Take(40))
        {
            output(
                $"ROW 0x{candidate.Address:X} score={candidate.Score} type={candidate.Type} " +
                $"completed={candidate.Completed} count={candidate.Count}/{candidate.Target} " +
                $"name='{candidate.Name}' description='{candidate.Description}' detail='{candidate.Detail}'");

            var rowReferences = FindPointerReferences(
                    memory,
                privateRegions,
                new HashSet<ulong> { candidate.Address },
                    1024)
                .Distinct()
                .ToList();
            output($"  row pointer references: {rowReferences.Count}");
            foreach (var array in FindContainingArrays(memory, regions, candidate.Address, rowReferences).Take(12))
            {
                output($"  ARRAY 0x{array.Address:X} count={array.Count} rowIndex={array.RowIndex}");
                for (var i = 0; i < array.RowAddresses.Count; i++)
                {
                    var rowAddress = array.RowAddresses[i];
                    if (TryReadJournalRow(memory, regions, rowAddress, null, out var sibling))
                    {
                        output(
                            $"    [{i}] 0x{rowAddress:X} type={sibling.Type} completed={sibling.Completed} " +
                            $"count={sibling.Count}/{sibling.Target} name='{sibling.Name}' " +
                            $"description='{sibling.Description}' detail='{sibling.Detail}'");
                    }
                    else
                    {
                        output($"    [{i}] 0x{rowAddress:X} (not a journal row)");
                    }
                }
            }
        }

        if (ordered.Count == 0)
        {
            output(
                "No exported-layout row validated. This means the visible tracker is likely using a different " +
                "client UI row; retain the string/reference counts for the next structural probe.");
        }

        if (!string.IsNullOrWhiteSpace(playerName))
        {
            ProbePlayerMissionTree(memory, regions, privateRegions, playerName, output);
        }
    }

    private static void ProbePlayerMissionTree(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        IReadOnlyList<MemoryRegion> scanRegions,
        string playerName,
        Action<string> output)
    {
        var nameAddresses = FindSubstringAddresses(memory, scanRegions, playerName, 4096).Distinct().ToList();
        output($"Player '{playerName}': {nameAddresses.Count} substring address candidate(s).");
        var entities = new HashSet<ulong>();
        foreach (var nameAddress in nameAddresses)
        {
            for (var nameOffset = 0; nameOffset < 128; nameOffset++)
            {
                var totalOffset = (ulong)(0x90 + nameOffset);
                if (nameAddress < totalOffset) continue;
                var entityAddress = nameAddress - totalOffset;
                if (entities.Contains(entityAddress)) continue;
                var entityData = new byte[304];
                if (!TryReadExact(memory, entityAddress, entityData)) continue;
                var entityType = BinaryPrimitives.ReadInt32LittleEndian(entityData.AsSpan(0x14, 4));
                var debugName = ReadInlineUtf8(entityData, 0x90, 128);
                var playerAddress = BinaryPrimitives.ReadUInt64LittleEndian(entityData.AsSpan(0x110, 8));
                if (entityType != 19 ||
                    !debugName.Contains(playerName, StringComparison.OrdinalIgnoreCase) ||
                    !IsPlausiblePointer(regions, playerAddress))
                {
                    continue;
                }
                entities.Add(entityAddress);
                output(
                    $"  ENTITY 0x{entityAddress:X} containerId=" +
                    $"{BinaryPrimitives.ReadInt32LittleEndian(entityData.AsSpan(0x10, 4))} " +
                    $"debugName='{debugName}' player=0x{playerAddress:X}");

                var playerData = new byte[352];
                if (!TryReadExact(memory, playerAddress, playerData))
                {
                    output("    Player structure unreadable.");
                    continue;
                }
                var missionInfoAddress = BinaryPrimitives.ReadUInt64LittleEndian(playerData.AsSpan(0x158, 8));
                output($"    MissionInfo=0x{missionInfoAddress:X}");
                if (!IsPlausiblePointer(regions, missionInfoAddress)) continue;

                var missionInfoData = new byte[320];
                if (!TryReadExact(memory, missionInfoAddress, missionInfoData)) continue;
                var missionsAddress = BinaryPrimitives.ReadUInt64LittleEndian(missionInfoData.AsSpan(0x00, 8));
                var primaryMission = ReadPointerString(memory, regions, missionInfoData, 0x40);
                var lastActiveMission = ReadPointerString(memory, regions, missionInfoData, 0xA0);
                var currentOpenMissionAddress = BinaryPrimitives.ReadUInt64LittleEndian(
                    missionInfoData.AsSpan(0xD0, 8));
                var currentOpenMission = ReadStringAt(memory, regions, currentOpenMissionAddress);
                var currentOpenMissionPartition = BinaryPrimitives.ReadInt32LittleEndian(
                    missionInfoData.AsSpan(0xD8, 4));
                var currentOpenMissionTimestamp = BinaryPrimitives.ReadInt32LittleEndian(
                    missionInfoData.AsSpan(0xDC, 4));
                var currentWaypointOpenMission = ReadPointerString(memory, regions, missionInfoData, 0xE0);
                output(
                    $"    Missions=0x{missionsAddress:X} primary='{primaryMission}' " +
                    $"lastActive='{lastActiveMission}'");
                output(
                    $"    CurrentOpenMission='{currentOpenMission}' (string=0x{currentOpenMissionAddress:X}) " +
                    $"partition={currentOpenMissionPartition} timestamp={currentOpenMissionTimestamp} " +
                    $"waypoint='{currentWaypointOpenMission}'");

                if (!string.IsNullOrWhiteSpace(currentOpenMission) &&
                    IsPlausiblePointer(regions, currentOpenMissionAddress))
                {
                    ProbeCurrentOpenMission(
                        memory,
                        regions,
                        scanRegions,
                        currentOpenMissionAddress,
                        currentOpenMission,
                        currentOpenMissionPartition,
                        output);
                }

                if (!TryReadEArrayPointers(memory, regions, missionsAddress, 4096, out var missions))
                {
                    output("    Missions e-array did not validate.");
                    continue;
                }
                output($"    Active mission root count: {missions.Count}");
                var primaryDumped = false;
                foreach (var missionAddress in missions)
                {
                    if (TryReadMissionAnyDefinition(memory, regions, missionAddress, out var primaryRoot) &&
                        string.Equals(
                            primaryRoot.Name,
                            primaryMission,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        output("    PRIMARY MISSION TREE:");
                        DumpMissionTree(
                            memory,
                            regions,
                            primaryRoot,
                            output,
                            "      ",
                            0,
                            new HashSet<ulong>());
                        primaryDumped = true;
                        break;
                    }
                }
                if (!primaryDumped)
                {
                    output("    Primary mission root was not found in the active mission e-array.");
                }
                foreach (var missionAddress in missions.Take(3))
                {
                    if (TryReadMissionAnyDefinition(memory, regions, missionAddress, out var mission))
                    {
                        output(
                            $"      ROOT 0x{mission.Address:X} name='{mission.Name}' state={mission.State} " +
                            $"children=0x{mission.Children:X}");
                    }
                    else
                    {
                        output($"      ROOT 0x{missionAddress:X} (invalid Mission layout)");
                    }
                }
            }
        }
        output($"Player '{playerName}': validated {entities.Count} Entity candidate(s).");
    }

    private static void ProbeCurrentOpenMission(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        IReadOnlyList<MemoryRegion> scanRegions,
        ulong currentNameAddress,
        string currentName,
        int expectedPartition,
        Action<string> output)
    {
        var nameReferences = FindPointerReferences(
                memory,
                scanRegions,
                new HashSet<ulong> { currentNameAddress },
                4096)
            .Distinct()
            .ToList();
        output($"    Current open-mission name has {nameReferences.Count} pointer reference(s).");

        var candidates = new HashSet<ulong>();
        foreach (var address in nameReferences)
        {
            var data = new byte[64];
            if (!TryReadExact(memory, address, data)) continue;
            var namePointer = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x00, 8));
            var missionAddress = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x08, 8));
            var partition = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x38, 4));
            if (namePointer != currentNameAddress ||
                !IsPlausiblePointer(regions, missionAddress) ||
                !TryReadMissionAnyDefinition(memory, regions, missionAddress, out var mission))
            {
                continue;
            }

            if (!candidates.Add(address)) continue;
            output(
                $"    OPENMISSION 0x{address:X} name='{currentName}' partition={partition} " +
                $"expectedPartition={expectedPartition} mission=0x{missionAddress:X}");
            DumpMissionTree(memory, regions, mission, output, "      ", 0, new HashSet<ulong>());
        }

        if (candidates.Count == 0)
        {
            output("    No live OpenMission structure validated from the current-name references.");
        }
    }

    private static string ReadInlineUtf8(byte[] data, int offset, int maximumLength)
    {
        var length = 0;
        while (length < maximumLength && data[offset + length] != 0) length++;
        try
        {
            return new UTF8Encoding(false, true).GetString(data, offset, length);
        }
        catch (DecoderFallbackException)
        {
            return string.Empty;
        }
    }

    private static bool TryReadJournalRow(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        ulong address,
        string? requiredText,
        out MissionJournalRowProbe row)
    {
        row = default!;
        var data = new byte[184];
        if (!TryReadExact(memory, address, data))
        {
            return false;
        }

        var type = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4));
        var completedByte = data[0x28];
        var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x64, 4));
        var target = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x68, 4));
        var showCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x6C, 4));
        var indent = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0xA0, 4));
        var uiType = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0xA4, 4));
        if (type is < -1 or > 256 || completedByte > 1 ||
            count is < -1 or > 10_000_000 || target is < -1 or > 10_000_000 ||
            showCount is < -1 or > 256 || indent is < -1 or > 64 || uiType is < -1 or > 4096)
        {
            return false;
        }

        var name = ReadPointerString(memory, regions, data, 0x30);
        var translatedMap = ReadPointerString(memory, regions, data, 0x38);
        var translatedCategory = ReadPointerString(memory, regions, data, 0x40);
        var logicalCategory = ReadPointerString(memory, regions, data, 0x48);
        var translatedUiType = ReadPointerString(memory, regions, data, 0x50);
        var iconName = ReadPointerString(memory, regions, data, 0x58);
        var description = ReadPointerString(memory, regions, data, 0x90);
        var detail = ReadPointerString(memory, regions, data, 0x98);
        var strings = new[]
        {
            name, translatedMap, translatedCategory, logicalCategory,
            translatedUiType, iconName, description, detail,
        };
        if (requiredText != null && !strings.Any(value =>
                value.Contains(requiredText, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        if (!strings.Any(LooksLikeNaturalText))
        {
            return false;
        }

        var score = strings.Count(value => !string.IsNullOrWhiteSpace(value)) * 10;
        score += strings.Count(LooksLikeNaturalText) * 10;
        if (requiredText != null)
        {
            score += strings.Count(value => string.Equals(value, requiredText, StringComparison.OrdinalIgnoreCase)) * 100;
            score += strings.Count(value => value.Contains(requiredText, StringComparison.OrdinalIgnoreCase)) * 40;
        }
        if (!string.IsNullOrWhiteSpace(name)) score += 20;
        if (!string.IsNullOrWhiteSpace(description)) score += 20;

        row = new MissionJournalRowProbe(
            address,
            score,
            type,
            completedByte != 0,
            count,
            target,
            showCount,
            indent,
            uiType,
            name,
            translatedMap,
            translatedCategory,
            logicalCategory,
            translatedUiType,
            iconName,
            description,
            detail);
        return true;
    }

    private static void ProbeMessageAndMissionDefinitions(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        IReadOnlyList<MemoryRegion> scanRegions,
        string anchor,
        IReadOnlySet<ulong> stringAddresses,
        IReadOnlyList<ulong> stringReferences,
        Action<string> output)
    {
        var messages = new Dictionary<ulong, ProbeMessage>();
        foreach (var reference in stringReferences)
        {
            if (reference < 0x20) continue;
            var address = reference - 0x20;
            if (TryReadMessage(memory, regions, address, anchor, stringAddresses, out var message))
            {
                messages[address] = message;
            }
        }
        output($"'{anchor}': {messages.Count} Message object candidate(s).");

        foreach (var message in messages.Values)
        {
            output(
                $"  MESSAGE 0x{message.Address:X} key='{message.Key}' " +
                $"file='{message.FileName}' default='{Escape(message.DefaultString)}'");
            var messageReferences = FindPointerReferences(
                    memory,
                    scanRegions,
                    new HashSet<ulong> { message.Address },
                    8192)
                .Distinct()
                .ToList();
            output($"    message pointer references: {messageReferences.Count}");
            foreach (var messageReference in messageReferences.Take(16))
            {
                output($"    MSGREF 0x{messageReference:X}");
                DumpPointerContext(memory, regions, scanRegions, messageReference, output);
            }

            var definitions = new Dictionary<ulong, ProbeMissionDef>();
            int[] messageOffsets = [0xB0, 0x108, 0x120, 0x138, 0x168, 0x180, 0x198, 0x1B0, 0x1C8, 0x1E0, 0x2A8];
            foreach (var reference in messageReferences)
            {
                foreach (var offset in messageOffsets)
                {
                    if (reference < (ulong)offset) continue;
                    var address = reference - (ulong)offset;
                    if (TryReadMissionDef(memory, regions, address, message.Address, out var definition))
                    {
                        definitions[address] = definition;
                    }
                }
            }

            output($"    MissionDef candidate(s): {definitions.Count}");
            foreach (var definition in definitions.Values)
            {
                output(
                    $"    DEF 0x{definition.Address:X} name='{definition.Name}' " +
                    $"file='{definition.FileName}' missionType={definition.MissionType} " +
                    $"uiType={definition.UiType} omit={definition.OmitFromTracker} icon='{definition.IconName}'");

                var definitionReferences = FindPointerReferences(
                        memory,
                        scanRegions,
                        new HashSet<ulong> { definition.Address },
                        8192)
                    .Distinct()
                    .ToList();
                output($"      definition pointer references: {definitionReferences.Count}");
                foreach (var definitionReference in definitionReferences.Take(16))
                {
                    output($"      DEFREF 0x{definitionReference:X}");
                    DumpDefinitionReferenceCandidates(memory, regions, definitionReference, output);
                    var slotReferences = FindPointerReferences(
                            memory,
                            scanRegions,
                            new HashSet<ulong> { definitionReference },
                            8192)
                        .Distinct()
                        .ToList();
                    output($"        references to DEFREF slot: {slotReferences.Count}");
                    foreach (var slotReference in slotReferences.Take(32))
                    {
                        output($"        DEFREFREF 0x{slotReference:X}");
                        DumpDefinitionReferenceCandidates(memory, regions, slotReference, output);
                    }
                }
                var missions = new Dictionary<ulong, ProbeMission>();
                foreach (var reference in definitionReferences)
                {
                    foreach (var offset in new[] { 0x70, 0x78 })
                    {
                        if (reference < (ulong)offset) continue;
                        var address = reference - (ulong)offset;
                        if (TryReadMission(memory, regions, address, definition.Address, out var mission))
                        {
                            missions[address] = mission;
                        }
                    }
                }
                output($"      live Mission candidate(s): {missions.Count}");
                foreach (var mission in missions.Values)
                {
                    DumpMissionTree(memory, regions, mission, output, "        ", 0, new HashSet<ulong>());
                }
            }
        }
    }

    private static bool TryReadMessage(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        ulong address,
        string anchor,
        IReadOnlySet<ulong> expectedStrings,
        out ProbeMessage message)
    {
        message = default!;
        var data = new byte[48];
        if (!TryReadExact(memory, address, data)) return false;
        var defaultAddress = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x20, 8));
        var defaultString = ReadStringAt(memory, regions, defaultAddress);
        if (!expectedStrings.Contains(defaultAddress) ||
            !string.Equals(defaultString, anchor, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        message = new ProbeMessage(
            address,
            ReadPointerString(memory, regions, data, 0x00),
            ReadPointerString(memory, regions, data, 0x08),
            defaultString);
        return true;
    }

    private static bool TryReadMissionDef(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        ulong address,
        ulong requiredMessage,
        out ProbeMissionDef definition)
    {
        definition = default!;
        var data = new byte[1232];
        if (!TryReadExact(memory, address, data)) return false;
        var version = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x24, 4));
        var missionType = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x40, 4));
        var uiType = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x4B8, 4));
        if (version is < 0 or > 1_000_000 || missionType is < -1 or > 512 ||
            uiType is < -1 or > 4096 || data[0x4A1] > 1)
        {
            return false;
        }

        int[] messageOffsets = [0xB0, 0x108, 0x120, 0x138, 0x168, 0x180, 0x198, 0x1B0, 0x1C8, 0x1E0, 0x2A8];
        if (!messageOffsets.Any(offset =>
                BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, 8)) == requiredMessage))
        {
            return false;
        }

        var name = ReadPointerString(memory, regions, data, 0x00);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 1024) return false;
        definition = new ProbeMissionDef(
            address,
            name,
            ReadPointerString(memory, regions, data, 0x10),
            missionType,
            ReadPointerString(memory, regions, data, 0x240),
            data[0x4A1] != 0,
            uiType);
        return true;
    }

    private static bool TryReadMission(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        ulong address,
        ulong requiredDefinition,
        out ProbeMission mission)
    {
        mission = default!;
        var data = new byte[192];
        if (!TryReadExact(memory, address, data)) return false;
        var state = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x38, 4));
        var rootDef = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x70, 8));
        var rootOverride = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x78, 8));
        var count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x98, 4));
        var target = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x9C, 4));
        if (state is < 0 or > 6 || (rootDef != requiredDefinition && rootOverride != requiredDefinition) ||
            data[0x55] > 1 || count is < -1 or > 10_000_000 || target is < -1 or > 10_000_000)
        {
            return false;
        }
        mission = new ProbeMission(
            address,
            ReadPointerString(memory, regions, data, 0x00),
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x10, 8)),
            state,
            data[0x55] != 0,
            rootDef,
            rootOverride,
            ReadPointerString(memory, regions, data, 0x60),
            ReadPointerString(memory, regions, data, 0x80),
            count,
            target,
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0xB4, 4)));
        return true;
    }

    private static void DumpDefinitionReferenceCandidates(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        ulong reference,
        Action<string> output)
    {
        foreach (var offset in new[] { 0x30, 0x70, 0x78 })
        {
            if (reference < (ulong)offset) continue;
            var address = reference - (ulong)offset;
            var bytes = new byte[192];
            if (!TryReadExact(memory, address, bytes)) continue;
            var name = ReadPointerString(memory, regions, bytes, 0x00);
            var children = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x10, 8));
            var state = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x38, 4));
            var tracking = bytes[0x55];
            var rootDef = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x70, 8));
            var rootOverride = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x78, 8));
            var nameOverride = ReadPointerString(memory, regions, bytes, 0x80);
            var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x98, 4));
            var target = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0x9C, 4));
            output(
                $"        base=REF-0x{offset:X}=0x{address:X}: name='{name}' children=0x{children:X} " +
                $"state={state} trackingByte={tracking} def=0x{rootDef:X}/0x{rootOverride:X} " +
                $"override='{nameOverride}' count={count}/{target}");
        }
    }

    private static void DumpMissionTree(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        ProbeMission mission,
        Action<string> output,
        string indent,
        int depth,
        HashSet<ulong> visited)
    {
        if (depth > 12 || !visited.Add(mission.Address)) return;
        output(
            $"{indent}MISSION 0x{mission.Address:X} name='{mission.Name}' state={mission.State} " +
            $"tracking={mission.Tracking} count={mission.Count}/{mission.Target} depth={mission.Depth} " +
            $"tracked='{mission.TrackedMission}' override='{mission.NameOverride}' " +
            $"def=0x{mission.RootDef:X}/0x{mission.RootOverride:X} children=0x{mission.Children:X}");

        if (!TryReadEArrayPointers(memory, regions, mission.Children, 256, out var children)) return;
        foreach (var childAddress in children)
        {
            if (TryReadMissionAnyDefinition(memory, regions, childAddress, out var child))
            {
                DumpMissionTree(memory, regions, child, output, indent + "  ", depth + 1, visited);
            }
            else
            {
                output($"{indent}  CHILD 0x{childAddress:X} (invalid Mission layout)");
            }
        }
    }

    private static bool TryReadMissionAnyDefinition(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        ulong address,
        out ProbeMission mission)
    {
        mission = default!;
        var data = new byte[192];
        if (!TryReadExact(memory, address, data)) return false;
        var definition = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x70, 8));
        var definitionOverride = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x78, 8));
        var required = definitionOverride != 0 ? definitionOverride : definition;
        if (!IsPlausiblePointer(regions, required)) return false;
        return TryReadMission(memory, regions, address, required, out mission);
    }

    private static bool TryReadEArrayPointers(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        ulong address,
        int maximumCount,
        out IReadOnlyList<ulong> values)
    {
        values = Array.Empty<ulong>();
        if (address == 0) return true;
        if (address < 0x10 || !TryReadInt32(memory, address - 0x10, out var count) ||
            count is < 0 || count > maximumCount)
        {
            return false;
        }
        var bytes = new byte[count * 8];
        if (count > 0 && !TryReadExact(memory, address, bytes)) return false;
        var result = new List<ulong>(count);
        for (var i = 0; i < count; i++)
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i * 8, 8));
            if (!IsPlausiblePointer(regions, value)) return false;
            result.Add(value);
        }
        values = result;
        return true;
    }

    private static IEnumerable<ProbeArray> FindContainingArrays(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        ulong rowAddress,
        IReadOnlyList<ulong> rowReferences)
    {
        var emitted = new HashSet<ulong>();
        foreach (var slot in rowReferences)
        {
            for (var assumedIndex = 0; assumedIndex < 64; assumedIndex++)
            {
                var byteOffset = (ulong)(assumedIndex * 8);
                if (slot < byteOffset + 0x10)
                {
                    continue;
                }
                var arrayAddress = slot - byteOffset;
                if (!TryReadInt32(memory, arrayAddress - 0x10, out var count) ||
                    count is < 1 or > 128 || assumedIndex >= count || !emitted.Add(arrayAddress))
                {
                    continue;
                }

                var bytes = new byte[count * 8];
                if (!TryReadExact(memory, arrayAddress, bytes))
                {
                    continue;
                }
                var addresses = new List<ulong>(count);
                var valid = true;
                for (var i = 0; i < count; i++)
                {
                    var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(i * 8, 8));
                    if (!IsPlausiblePointer(regions, value))
                    {
                        valid = false;
                        break;
                    }
                    addresses.Add(value);
                }
                if (valid && addresses[assumedIndex] == rowAddress)
                {
                    yield return new ProbeArray(arrayAddress, count, assumedIndex, addresses);
                }
            }
        }
    }

    private static IEnumerable<ulong> FindExactStringAddresses(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        string value,
        int maximumMatches)
    {
        foreach (var pattern in new[]
                 {
                     Encoding.UTF8.GetBytes(value + "\0"),
                     Encoding.Unicode.GetBytes(value + "\0"),
                 })
        {
            foreach (var address in FindPattern(memory, regions, pattern, maximumMatches))
            {
                yield return address;
            }
        }
    }

    private static IEnumerable<ulong> FindSubstringAddresses(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        string value,
        int maximumMatches)
    {
        foreach (var pattern in new[]
                 {
                     Encoding.UTF8.GetBytes(value),
                     Encoding.Unicode.GetBytes(value),
                 })
        {
            foreach (var address in FindPattern(memory, regions, pattern, maximumMatches))
            {
                yield return address;
            }
        }
    }

    private static IEnumerable<ulong> FindPattern(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        byte[] pattern,
        int maximumMatches)
    {
        var found = 0;
        var overlap = Math.Max(0, pattern.Length - 1);
        foreach (var region in regions)
        {
            ulong regionOffset = 0;
            var carry = Array.Empty<byte>();
            while (regionOffset < region.Size)
            {
                var requested = (int)Math.Min((ulong)ScanBlockSize, region.Size - regionOffset);
                var block = new byte[carry.Length + requested];
                if (carry.Length > 0)
                {
                    Buffer.BlockCopy(carry, 0, block, 0, carry.Length);
                }
                if (!memory.TryRead(region.BaseAddress + regionOffset, block, carry.Length, requested, out var read) || read <= 0)
                {
                    regionOffset += (ulong)requested;
                    carry = Array.Empty<byte>();
                    continue;
                }

                var length = carry.Length + read;
                var searchStart = 0;
                while (searchStart <= length - pattern.Length)
                {
                    var index = block.AsSpan(searchStart, length - searchStart).IndexOf(pattern);
                    if (index < 0) break;
                    index += searchStart;
                    yield return region.BaseAddress + regionOffset - (ulong)carry.Length + (ulong)index;
                    if (++found >= maximumMatches) yield break;
                    searchStart = index + 1;
                }

                var carryLength = Math.Min(overlap, length);
                carry = block.AsSpan(length - carryLength, carryLength).ToArray();
                regionOffset += (ulong)read;
                if (read < requested) regionOffset += (ulong)(requested - read);
            }
        }
    }

    private static IEnumerable<ulong> FindPointerReferences(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        IReadOnlySet<ulong> pointers,
        int maximumMatches)
    {
        if (pointers.Count == 0) yield break;
        var found = 0;
        foreach (var region in regions)
        {
            ulong regionOffset = 0;
            while (regionOffset < region.Size)
            {
                var requested = (int)Math.Min((ulong)ScanBlockSize, region.Size - regionOffset);
                var block = new byte[requested];
                var blockAddress = region.BaseAddress + regionOffset;
                if (!memory.TryRead(blockAddress, block, 0, requested, out var read) || read < 8)
                {
                    regionOffset += (ulong)requested;
                    continue;
                }
                var alignment = (int)((8 - (blockAddress & 7)) & 7);
                for (var offset = alignment; offset <= read - 8; offset += 8)
                {
                    var value = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(offset, 8));
                    if (!pointers.Contains(value)) continue;
                    yield return blockAddress + (ulong)offset;
                    if (++found >= maximumMatches) yield break;
                }
                regionOffset += (ulong)requested;
            }
        }
    }

    private static string ReadPointerString(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        byte[] container,
        int offset)
    {
        var address = BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(offset, 8));
        return ReadStringAt(memory, regions, address);
    }

    private static string ReadStringAt(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        ulong address)
    {
        if (address == 0) return string.Empty;
        if (!IsPlausiblePointer(regions, address)) return string.Empty;
        var bytes = new byte[1024];
        if (!memory.TryRead(address, bytes, 0, bytes.Length, out var read) || read <= 0) return string.Empty;

        try
        {
            if (read >= 2 && bytes[1] == 0)
            {
                var end = 0;
                while (end + 1 < read && (bytes[end] != 0 || bytes[end + 1] != 0)) end += 2;
                return Encoding.Unicode.GetString(bytes, 0, end);
            }
            var utf8End = Array.IndexOf(bytes, (byte)0, 0, read);
            if (utf8End < 0) return string.Empty;
            return new UTF8Encoding(false, true).GetString(bytes, 0, utf8End);
        }
        catch (DecoderFallbackException)
        {
            return string.Empty;
        }
    }

    private static void DumpPointerContext(
        IMemoryReader memory,
        IReadOnlyList<MemoryRegion> regions,
        IReadOnlyList<MemoryRegion> scanRegions,
        ulong reference,
        Action<string> output)
    {
        const int before = 0x100;
        const int size = 0x240;
        if (reference < before) return;
        var start = reference - before;
        var bytes = new byte[size];
        if (!TryReadExact(memory, start, bytes)) return;

        var shownPointers = 0;
        for (var offset = 0; offset <= bytes.Length - 8; offset += 8)
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(offset, 8));
            if (!IsPlausiblePointer(regions, value)) continue;
            var relative = (long)(start + (ulong)offset) - (long)reference;
            var text = ReadStringAt(memory, regions, value);
            if (!string.IsNullOrWhiteSpace(text) && text.Length <= 512)
            {
                output($"    {relative,5:X}: 0x{value:X} string='{Escape(text)}'");
            }
            else if (shownPointers < 24)
            {
                output($"    {relative,5:X}: 0x{value:X} pointer");
                shownPointers++;
            }
        }

        var directReferences = FindPointerReferences(
                memory,
                scanRegions,
                new HashSet<ulong> { reference },
                128)
            .Distinct()
            .ToList();
        output($"    direct references to TEXTREF slot: {directReferences.Count}");
    }

    private static string Escape(string value) => value
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal)
        .Replace("\t", "\\t", StringComparison.Ordinal);

    private static bool TryReadExact(IMemoryReader memory, ulong address, byte[] bytes) =>
        memory.TryRead(address, bytes, 0, bytes.Length, out var read) && read == bytes.Length;

    private static bool TryReadInt32(IMemoryReader memory, ulong address, out int value)
    {
        var bytes = new byte[4];
        if (!TryReadExact(memory, address, bytes))
        {
            value = 0;
            return false;
        }
        value = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        return true;
    }

    private static bool TryReadUInt64(IMemoryReader memory, ulong address, out ulong value)
    {
        var bytes = new byte[8];
        if (!TryReadExact(memory, address, bytes))
        {
            value = 0;
            return false;
        }
        value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        return true;
    }

    private static bool IsPlausiblePointer(IReadOnlyList<MemoryRegion> regions, ulong address)
    {
        if (address < 0x10000 || address > 0x00007FFFFFFFFFFF) return false;
        var lo = 0;
        var hi = regions.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var region = regions[mid];
            if (address < region.BaseAddress) hi = mid - 1;
            else if (address >= region.EndAddress) lo = mid + 1;
            else return true;
        }
        return false;
    }

    private static bool LooksLikeNaturalText(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) return false;
        var letters = value.Count(char.IsLetter);
        return letters >= 3 && letters * 2 >= Math.Min(value.Length, 40);
    }

    private sealed record MissionJournalRowProbe(
        ulong Address,
        int Score,
        int Type,
        bool Completed,
        int Count,
        int Target,
        int ShowCount,
        int Indent,
        int UiType,
        string Name,
        string TranslatedMap,
        string TranslatedCategory,
        string LogicalCategory,
        string TranslatedUiType,
        string IconName,
        string Description,
        string Detail);

    private sealed record ProbeArray(
        ulong Address,
        int Count,
        int RowIndex,
        IReadOnlyList<ulong> RowAddresses);

    private sealed record ProbeMessage(
        ulong Address,
        string Key,
        string FileName,
        string DefaultString);

    private sealed record ProbeMissionDef(
        ulong Address,
        string Name,
        string FileName,
        int MissionType,
        string IconName,
        bool OmitFromTracker,
        int UiType);

    private sealed record ProbeMission(
        ulong Address,
        string Name,
        ulong Children,
        int State,
        bool Tracking,
        ulong RootDef,
        ulong RootOverride,
        string TrackedMission,
        string NameOverride,
        int Count,
        int Target,
        int Depth);
}
