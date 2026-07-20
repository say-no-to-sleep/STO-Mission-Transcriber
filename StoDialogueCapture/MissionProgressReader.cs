using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace StoDialogueCapture;

/// <summary>
/// Reads the player's current OpenMission and its live Mission tree. All access is
/// read-only. Cryptic e-arrays store their element count 0x10 bytes before the data.
/// </summary>
internal sealed class MissionProgressReader
{
    private const int ScanBlockSize = 1024 * 1024;
    private const uint MemPrivate = 0x20000;

    private readonly IMemoryReader _memory;
    private readonly Dictionary<string, MissionDefinition> _definitions =
        new(StringComparer.OrdinalIgnoreCase);
    private ulong _missionInfoAddress;
    private ulong _openMissionAddress;
    private ulong _openMissionNameAddress;
    private string? _trackedPrimaryMissionName;
    private string? _lastOpenMissionInternalName;
    private string? _lastOpenMissionTitle;
    private bool _primaryWrapperActive;
    private int _failedOpenMissionDiscoveries;
    private DateTimeOffset _nextPlayerDiscovery = DateTimeOffset.MinValue;
    private DateTimeOffset _nextOpenMissionDiscovery = DateTimeOffset.MinValue;

    public MissionProgressReader(IMemoryReader memory)
    {
        _memory = memory;
    }

    public MissionProgressSnapshot? TryRead(string processName, Action<string>? progress = null)
    {
        var regions = ReadableRegions();
        if (!TryReadMissionInfo(regions, _missionInfoAddress, out var missionInfo))
        {
            _missionInfoAddress = 0;
            _openMissionAddress = 0;
            if (DateTimeOffset.UtcNow < _nextPlayerDiscovery)
            {
                return null;
            }

            _nextPlayerDiscovery = DateTimeOffset.UtcNow.AddSeconds(3);
            _memory.RefreshRegions();
            regions = ReadableRegions();
            _missionInfoAddress = DiscoverCurrentMissionInfo(regions);
            if (_missionInfoAddress == 0 ||
                !TryReadMissionInfo(regions, _missionInfoAddress, out missionInfo))
            {
                progress?.Invoke("Mission progress: the current player MissionInfo was not found yet.");
                return null;
            }
            progress?.Invoke($"Mission progress: found player MissionInfo at 0x{_missionInfoAddress:X}.");
        }

        if (!string.IsNullOrWhiteSpace(missionInfo.PrimaryMission) &&
            !string.Equals(
                missionInfo.PrimaryMission,
                _trackedPrimaryMissionName,
                StringComparison.OrdinalIgnoreCase))
        {
            _trackedPrimaryMissionName = missionInfo.PrimaryMission;
            _primaryWrapperActive = false;
            _lastOpenMissionInternalName = null;
            _lastOpenMissionTitle = null;
            _openMissionAddress = 0;
            _openMissionNameAddress = 0;
            _failedOpenMissionDiscoveries = 0;
        }

        var newOpenMissionAvailable =
            !string.IsNullOrWhiteSpace(missionInfo.CurrentOpenMissionName) &&
            !string.Equals(
                missionInfo.CurrentOpenMissionName,
                _lastOpenMissionInternalName,
                StringComparison.OrdinalIgnoreCase);
        if (_primaryWrapperActive && newOpenMissionAvailable)
        {
            // Multi-part episodes replace one OpenMission with another after a
            // wrapper section gate (space -> ground, for example). The wrapper
            // latch only blocks the completed old OpenMission; it must yield as
            // soon as MissionInfo advertises a genuinely new internal name.
            _primaryWrapperActive = false;
            _openMissionAddress = 0;
            _openMissionNameAddress = 0;
            _lastOpenMissionTitle = null;
        }
        else if (_primaryWrapperActive)
        {
            // After the surrounding episode advances to its next section, the
            // completed OpenMission can remain allocated. Never fall back to it:
            // doing so makes an obsolete checklist reappear in the transcript.
            return TryReadTrackedPrimaryMission(
                regions,
                missionInfo,
                processName,
                _lastOpenMissionTitle);
        }

        if (missionInfo.CurrentOpenMissionNameAddress == 0 ||
            string.IsNullOrWhiteSpace(missionInfo.CurrentOpenMissionName))
        {
            _openMissionAddress = 0;
            _openMissionNameAddress = 0;
            if (DateTimeOffset.UtcNow >= _nextPlayerDiscovery)
            {
                // A map transition can leave the old, still-readable player
                // MissionInfo cached briefly. Re-score player entities while no
                // OpenMission is exposed so the new map is acquired promptly.
                _nextPlayerDiscovery = DateTimeOffset.UtcNow.AddSeconds(3);
                _memory.RefreshRegions();
                regions = ReadableRegions();
                var rediscovered = DiscoverCurrentMissionInfo(regions);
                if (rediscovered != 0 && rediscovered != _missionInfoAddress &&
                    TryReadMissionInfo(regions, rediscovered, out var refreshedInfo))
                {
                    _missionInfoAddress = rediscovered;
                    missionInfo = refreshedInfo;
                    progress?.Invoke(
                        $"Mission progress: reacquired player MissionInfo at 0x{_missionInfoAddress:X}.");
                    if (missionInfo.CurrentOpenMissionNameAddress != 0 &&
                        !string.IsNullOrWhiteSpace(missionInfo.CurrentOpenMissionName))
                    {
                        return TryRead(processName, progress);
                    }
                }
            }
            return string.IsNullOrWhiteSpace(_lastOpenMissionTitle)
                ? null
                : TryReadTrackedPrimaryMission(
                    regions,
                    missionInfo,
                    processName,
                    _lastOpenMissionTitle);
        }

        if (_openMissionNameAddress != missionInfo.CurrentOpenMissionNameAddress ||
            !TryReadOpenMission(
                regions,
                _openMissionAddress,
                missionInfo.CurrentOpenMissionNameAddress,
                missionInfo.CurrentOpenMissionName,
                out var root))
        {
            _openMissionAddress = 0;
            _openMissionNameAddress = missionInfo.CurrentOpenMissionNameAddress;
            if (DateTimeOffset.UtcNow < _nextOpenMissionDiscovery)
            {
                return null;
            }

            _nextOpenMissionDiscovery = DateTimeOffset.UtcNow.AddSeconds(5);
            _memory.RefreshRegions();
            regions = ReadableRegions();
            _openMissionAddress = DiscoverOpenMission(
                regions,
                missionInfo.CurrentOpenMissionNameAddress,
                missionInfo.CurrentOpenMissionName,
                out root);
            if (_openMissionAddress == 0)
            {
                _failedOpenMissionDiscoveries++;
                if (_failedOpenMissionDiscoveries >= 2)
                {
                    // The name can survive a map transition while its owning
                    // MissionInfo does not. Force a fresh player search instead
                    // of retrying the same stale owner indefinitely.
                    _missionInfoAddress = 0;
                    _nextPlayerDiscovery = DateTimeOffset.MinValue;
                    _failedOpenMissionDiscoveries = 0;
                }
                progress?.Invoke(
                    $"Mission progress: waiting for '{missionInfo.CurrentOpenMissionName}' to become readable.");
                return null;
            }
            _failedOpenMissionDiscoveries = 0;
            progress?.Invoke(
                $"Mission progress: following '{missionInfo.CurrentOpenMissionName}' at " +
                $"0x{_openMissionAddress:X}.");
        }

        var nodes = new List<MissionNode>();
        if (!ReadMissionTree(regions, root.Address, 0, new HashSet<ulong>(), nodes))
        {
            _openMissionAddress = 0;
            return null;
        }

        var openSnapshot = BuildSnapshot(
            regions,
            root,
            nodes,
            processName,
            _openMissionAddress,
            null);
        _lastOpenMissionInternalName = missionInfo.CurrentOpenMissionName;
        _lastOpenMissionTitle = openSnapshot.MissionTitle;

        // Once the OpenMission succeeds, STO's HUD can advance to a child of
        // the surrounding episode mission (for example, "Return to Sector Space").
        if (root.State == 1)
        {
            var primary = TryReadTrackedPrimaryMission(
                regions,
                missionInfo,
                processName,
                _lastOpenMissionTitle);
            if (primary != null)
            {
                _primaryWrapperActive = true;
                return primary;
            }
            // The OpenMission has finished but its primary wrapper has not yet
            // advanced to a real HUD action. Do not briefly resurrect the
            // completed OpenMission checklist while waiting for that handoff.
            return null;
        }
        return openSnapshot;
    }

    private MissionProgressSnapshot? TryReadTrackedPrimaryMission(
        IReadOnlyList<MemoryRegion> regions,
        MissionInfoView missionInfo,
        string processName,
        string? titleOverride)
    {
        if (string.IsNullOrWhiteSpace(_trackedPrimaryMissionName) ||
            !string.Equals(
                missionInfo.PrimaryMission,
                _trackedPrimaryMissionName,
                StringComparison.OrdinalIgnoreCase) ||
            !TryReadEArrayPointers(regions, missionInfo.MissionsAddress, 4096, out var missionRoots))
        {
            return null;
        }

        foreach (var rootAddress in missionRoots)
        {
            if (!TryReadMissionNode(regions, rootAddress, out var root) ||
                !string.Equals(
                    root.InternalName,
                    _trackedPrimaryMissionName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var nodes = new List<MissionNode>();
            if (!ReadMissionTree(regions, root.Address, 0, new HashSet<ulong>(), nodes)) return null;
            var wrapper = BuildSnapshot(regions, root, nodes, processName, root.Address, titleOverride);
            var visibleObjectives = wrapper.Objectives
                .Select(objective => objective with
                {
                    VisibleInTracker = objective.VisibleInTracker &&
                                       objective.State == 0 &&
                                       !string.Equals(
                                           TranscriptRenderer.CleanText(objective.DisplayText),
                                           TranscriptRenderer.CleanText(titleOverride),
                                           StringComparison.OrdinalIgnoreCase),
                })
                .ToList();
            if (wrapper.State == 0 && visibleObjectives.All(objective => !objective.VisibleInTracker))
            {
                // A child whose label merely repeats the current OpenMission
                // title is a section gate, not a checkbox objective.
                return null;
            }
            return wrapper with
            {
                // Primary-mission children are section gates, not the objective
                // history shown inside an OpenMission. STO displays the current
                // gate only (for example, Part_2 -> Return to Sector Space).
                Objectives = visibleObjectives,
            };
        }
        return null;
    }

    private MissionProgressSnapshot BuildSnapshot(
        IReadOnlyList<MemoryRegion> regions,
        MissionNode root,
        IReadOnlyList<MissionNode> nodes,
        string processName,
        ulong address,
        string? titleOverride)
    {
        ResolveDefinitions(regions, nodes);
        _definitions.TryGetValue(root.InternalName, out var rootDefinition);
        var title = FirstNonempty(
            titleOverride,
            rootDefinition?.DisplayName,
            rootDefinition?.UiString,
            root.NameOverride,
            root.InternalName);

        var objectives = nodes
            .Skip(1)
            .Select(node =>
            {
                _definitions.TryGetValue(node.InternalName, out var definition);
                var rawDisplay = FirstNonempty(
                    definition?.UiString,
                    definition?.DisplayName,
                    node.NameOverride,
                    node.InternalName);
                var display = TranscriptRenderer.CleanText(rawDisplay);
                var omit = definition?.OmitFromTracker ?? false;
                var hasTrackerLabel = !string.IsNullOrWhiteSpace(definition?.UiString) ||
                                      !string.IsNullOrWhiteSpace(definition?.DisplayName) ||
                                      !string.IsNullOrWhiteSpace(node.NameOverride);
                var (markerStyle, markerText) = ReadMarker(rawDisplay);
                return new MissionObjectiveSnapshot(
                    node.InternalName,
                    display,
                    rawDisplay,
                    node.State,
                    StateName(node.State),
                    node.Count,
                    node.Target,
                    node.Depth,
                    node.DisplayOrder,
                    node.Tracking,
                    node.Hidden,
                    omit,
                    !omit && !node.Hidden && hasTrackerLabel,
                    definition?.MissionUiType ?? 0,
                    definition?.IconName ?? string.Empty,
                    definition?.ObjectiveTags ?? [],
                    markerStyle,
                    markerText,
                    InferRecruitmentType(root.InternalName, node.InternalName, markerStyle),
                    node.ParentInternalName,
                    node.TraversalOrder);
            })
            .ToList();

        return new MissionProgressSnapshot(
            DateTimeOffset.UtcNow,
            "missionProgress",
            processName,
            $"0x{address:X}",
            root.InternalName,
            title,
            root.State,
            StateName(root.State),
            objectives);
    }

    private IReadOnlyList<MemoryRegion> ReadableRegions() => _memory.Regions
        .Where(region => region.IsReadable)
        .OrderBy(region => region.BaseAddress)
        .ToList();

    private ulong DiscoverCurrentMissionInfo(IReadOnlyList<MemoryRegion> regions)
    {
        var scanRegions = regions.Where(region => region.Type == MemPrivate).ToList();
        var bestAddress = 0UL;
        var bestScore = int.MinValue;
        foreach (var debugNameAddress in FindPattern(scanRegions, Encoding.UTF8.GetBytes("P["), int.MaxValue))
        {
            if (debugNameAddress < 0x90) continue;
            var entityAddress = debugNameAddress - 0x90;
            var entity = new byte[0x118];
            if (!TryReadExact(entityAddress, entity) ||
                BinaryPrimitives.ReadInt32LittleEndian(entity.AsSpan(0x14, 4)) != 19)
            {
                continue;
            }
            var playerAddress = BinaryPrimitives.ReadUInt64LittleEndian(entity.AsSpan(0x110, 8));
            if (!IsPlausiblePointer(regions, playerAddress)) continue;
            var player = new byte[0x160];
            if (!TryReadExact(playerAddress, player)) continue;
            var missionInfoAddress = BinaryPrimitives.ReadUInt64LittleEndian(player.AsSpan(0x158, 8));
            if (!TryReadMissionInfo(regions, missionInfoAddress, out var info)) continue;

            var score = 0;
            if (!string.IsNullOrWhiteSpace(info.PrimaryMission)) score += 20;
            if (!string.IsNullOrWhiteSpace(info.CurrentOpenMissionName)) score += 100;
            if (TryReadEArrayPointers(regions, info.MissionsAddress, 4096, out var roots) && roots.Count > 0)
            {
                score += 20;
                if (TryReadMissionNode(regions, roots[0], out _)) score += 100;
            }
            if (score > bestScore)
            {
                bestScore = score;
                bestAddress = missionInfoAddress;
                if (score >= 240)
                {
                    return bestAddress;
                }
            }
        }
        return bestAddress;
    }

    private bool TryReadMissionInfo(
        IReadOnlyList<MemoryRegion> regions,
        ulong address,
        out MissionInfoView info)
    {
        info = default!;
        if (!IsPlausiblePointer(regions, address)) return false;
        var data = new byte[0xE8];
        if (!TryReadExact(address, data)) return false;
        var missionsAddress = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x00, 8));
        if (!TryReadEArrayPointers(regions, missionsAddress, 4096, out _)) return false;
        info = new MissionInfoView(
            missionsAddress,
            ReadPointerString(regions, data, 0x40),
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0xD0, 8)),
            ReadPointerString(regions, data, 0xD0));
        return true;
    }

    private ulong DiscoverOpenMission(
        IReadOnlyList<MemoryRegion> regions,
        ulong nameAddress,
        string name,
        out MissionNode root)
    {
        root = default!;
        var scanRegions = regions.Where(region => region.Type == MemPrivate).ToList();
        foreach (var reference in FindPointerReferences(scanRegions, new HashSet<ulong> { nameAddress }, 4096))
        {
            if (TryReadOpenMission(regions, reference, nameAddress, name, out root))
            {
                return reference;
            }
        }
        return 0;
    }

    private bool TryReadOpenMission(
        IReadOnlyList<MemoryRegion> regions,
        ulong address,
        ulong expectedNameAddress,
        string expectedName,
        out MissionNode root)
    {
        root = default!;
        if (!IsPlausiblePointer(regions, address)) return false;
        var data = new byte[0x40];
        if (!TryReadExact(address, data) ||
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x00, 8)) != expectedNameAddress)
        {
            return false;
        }
        var missionAddress = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x08, 8));
        return TryReadMissionNode(regions, missionAddress, out root) &&
               string.Equals(root.InternalName, expectedName, StringComparison.OrdinalIgnoreCase);
    }

    private bool ReadMissionTree(
        IReadOnlyList<MemoryRegion> regions,
        ulong address,
        int traversalDepth,
        HashSet<ulong> visited,
        List<MissionNode> nodes,
        string parentInternalName = "")
    {
        if (traversalDepth > 16 || nodes.Count >= 256 || !visited.Add(address) ||
            !TryReadMissionNode(regions, address, out var node))
        {
            return false;
        }
        node = node with
        {
            ParentInternalName = parentInternalName,
            TraversalOrder = nodes.Count,
        };
        nodes.Add(node);
        // Mission trees mutate while objectives activate. A single transient
        // child must not discard the otherwise readable tracker snapshot.
        if (!TryReadEArrayPointers(regions, node.ChildrenAddress, 256, out var children))
        {
            return traversalDepth > 0;
        }
        foreach (var child in children)
        {
            ReadMissionTree(
                regions,
                child,
                traversalDepth + 1,
                visited,
                nodes,
                node.InternalName);
        }
        return true;
    }

    private bool TryReadMissionNode(
        IReadOnlyList<MemoryRegion> regions,
        ulong address,
        out MissionNode node)
    {
        node = default!;
        if (!IsPlausiblePointer(regions, address)) return false;
        var data = new byte[192];
        if (!TryReadExact(address, data)) return false;
        var nameAddress = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x00, 8));
        var name = ReadStringAt(regions, nameAddress);
        var state = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x38, 4));
        var rootDefinition = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x70, 8));
        var rootOverride = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x78, 8));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 512 || state is < 0 or > 6 ||
            !IsPlausiblePointer(regions, rootOverride != 0 ? rootOverride : rootDefinition))
        {
            return false;
        }
        node = new MissionNode(
            address,
            nameAddress,
            name,
            BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x10, 8)),
            state,
            data[0x55] != 0,
            data[0x5A] != 0,
            rootDefinition,
            rootOverride,
            ReadPointerString(regions, data, 0x80),
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x98, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x9C, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0xB4, 4)),
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x20, 4)));
        return true;
    }

    private void ResolveDefinitions(IReadOnlyList<MemoryRegion> regions, IReadOnlyList<MissionNode> nodes)
    {
        // Rootdef is a direct MissionDef pointer. This immediately resolves the title.
        foreach (var node in nodes)
        {
            var direct = node.RootOverride != 0 ? node.RootOverride : node.RootDefinition;
            if (!_definitions.ContainsKey(node.InternalName) &&
                TryReadMissionDefinition(regions, direct, node.InternalName, 0, out var directDefinition))
            {
                _definitions[node.InternalName] = directDefinition;
            }
        }

        var unresolved = nodes
            .Where(node => !_definitions.ContainsKey(node.InternalName))
            .GroupBy(node => node.NameAddress)
            .ToDictionary(group => group.Key, group => group.First());
        if (unresolved.Count == 0) return;

        _memory.RefreshRegions();
        regions = ReadableRegions();
        var scanRegions = regions.Where(region => region.Type == MemPrivate).ToList();
        foreach (var reference in FindPointerReferences(scanRegions, unresolved.Keys.ToHashSet(), 16384))
        {
            if (!TryReadUInt64(reference, out var nameAddress) ||
                !unresolved.TryGetValue(nameAddress, out var node) ||
                !TryReadMissionDefinition(
                    regions,
                    reference,
                    node.InternalName,
                    node.Depth == 1
                        ? (node.RootOverride != 0 ? node.RootOverride : node.RootDefinition)
                        : 0,
                    out var definition))
            {
                continue;
            }
            _definitions[node.InternalName] = definition;
            unresolved.Remove(nameAddress);
            if (unresolved.Count == 0) break;
        }

        if (unresolved.Count == 0) return;

        // Generic names such as Part_1/Part_2 are shared by many missions and
        // may exhaust/name-collide in the pool-string scan. Resolve direct
        // children by MissionDef.Parentdef instead.
        var rootDefinitions = unresolved.Values
            .Where(node => node.Depth == 1)
            .Select(node => node.RootOverride != 0 ? node.RootOverride : node.RootDefinition)
            .Where(address => address != 0)
            .ToHashSet();
        foreach (var parentReference in FindPointerReferences(scanRegions, rootDefinitions, 16384))
        {
            if (parentReference < 0x30) continue;
            var candidateAddress = parentReference - 0x30;
            var candidateBytes = new byte[8];
            if (!TryReadExact(candidateAddress, candidateBytes)) continue;
            var candidateName = ReadStringAt(
                regions,
                BinaryPrimitives.ReadUInt64LittleEndian(candidateBytes));
            var node = unresolved.Values.FirstOrDefault(value =>
                value.Depth == 1 &&
                string.Equals(value.InternalName, candidateName, StringComparison.OrdinalIgnoreCase));
            if (node == null) continue;
            var expectedParent = node.RootOverride != 0 ? node.RootOverride : node.RootDefinition;
            if (!TryReadMissionDefinition(
                    regions,
                    candidateAddress,
                    node.InternalName,
                    expectedParent,
                    out var definition))
            {
                continue;
            }
            _definitions[node.InternalName] = definition;
            unresolved.Remove(node.NameAddress);
            if (unresolved.Count == 0) break;
        }
    }

    private bool TryReadMissionDefinition(
        IReadOnlyList<MemoryRegion> regions,
        ulong address,
        string expectedName,
        ulong expectedParent,
        out MissionDefinition definition)
    {
        definition = default!;
        if (!IsPlausiblePointer(regions, address)) return false;
        var data = new byte[1232];
        if (!TryReadExact(address, data)) return false;
        var name = ReadPointerString(regions, data, 0x00);
        var fileName = ReadPointerString(regions, data, 0x10);
        var version = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x24, 4));
        var parentDefinition = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(0x30, 8));
        if (!string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase) ||
            !fileName.Contains("Mission", StringComparison.OrdinalIgnoreCase) ||
            version is < 0 or > 100_000 ||
            (expectedParent != 0 && parentDefinition != expectedParent))
        {
            return false;
        }

        definition = new MissionDefinition(
            address,
            name,
            ReadDisplayMessage(regions, data, 0x108),
            ReadDisplayMessage(regions, data, 0x120),
            ReadPointerString(regions, data, 0x240),
            data[0x4A1] != 0,
            BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0x4B8, 4)),
            ReadEArrayInt32(data, 0x4B0, 64));
        return true;
    }

    private string ReadDisplayMessage(IReadOnlyList<MemoryRegion> regions, byte[] container, int offset)
    {
        var messageAddress = BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(offset, 8));
        if (!IsPlausiblePointer(regions, messageAddress)) return string.Empty;
        var message = new byte[48];
        return TryReadExact(messageAddress, message)
            ? ReadPointerString(regions, message, 0x20)
            : string.Empty;
    }

    private IReadOnlyList<int> ReadEArrayInt32(byte[] container, int offset, int maximumCount)
    {
        var address = BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(offset, 8));
        if (address == 0 || address < 0x10 || !TryReadInt32(address - 0x10, out var count) ||
            count is < 0 || count > maximumCount)
        {
            return [];
        }
        var data = new byte[count * 4];
        if (count > 0 && !TryReadExact(address, data)) return [];
        var result = new List<int>(count);
        for (var i = 0; i < count; i++)
        {
            result.Add(BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i * 4, 4)));
        }
        return result;
    }

    private bool TryReadEArrayPointers(
        IReadOnlyList<MemoryRegion> regions,
        ulong address,
        int maximumCount,
        out IReadOnlyList<ulong> values)
    {
        values = [];
        if (address == 0) return true;
        if (address < 0x10 || !TryReadInt32(address - 0x10, out var count) ||
            count is < 0 || count > maximumCount)
        {
            return false;
        }
        var data = new byte[count * 8];
        if (count > 0 && !TryReadExact(address, data)) return false;
        var result = new List<ulong>(count);
        for (var i = 0; i < count; i++)
        {
            var value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(i * 8, 8));
            if (!IsPlausiblePointer(regions, value)) return false;
            result.Add(value);
        }
        values = result;
        return true;
    }

    private IEnumerable<ulong> FindPattern(
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
                if (carry.Length > 0) Buffer.BlockCopy(carry, 0, block, 0, carry.Length);
                if (!_memory.TryRead(region.BaseAddress + regionOffset, block, carry.Length, requested, out var read) ||
                    read <= 0)
                {
                    regionOffset += (ulong)requested;
                    carry = [];
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

    private IEnumerable<ulong> FindPointerReferences(
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
                if (!_memory.TryRead(blockAddress, block, 0, requested, out var read) || read < 8)
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

    private string ReadPointerString(IReadOnlyList<MemoryRegion> regions, byte[] container, int offset) =>
        ReadStringAt(
            regions,
            BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(offset, 8)));

    private string ReadStringAt(IReadOnlyList<MemoryRegion> regions, ulong address)
    {
        if (!IsPlausiblePointer(regions, address)) return string.Empty;
        var bytes = new byte[4096];
        if (!_memory.TryRead(address, bytes, 0, bytes.Length, out var read) || read <= 0) return string.Empty;
        try
        {
            var end = Array.IndexOf(bytes, (byte)0, 0, read);
            return end < 0 ? string.Empty : new UTF8Encoding(false, true).GetString(bytes, 0, end);
        }
        catch (DecoderFallbackException)
        {
            return string.Empty;
        }
    }

    private bool TryReadExact(ulong address, byte[] data) =>
        _memory.TryRead(address, data, 0, data.Length, out var read) && read == data.Length;

    private bool TryReadInt32(ulong address, out int value)
    {
        var data = new byte[4];
        if (!TryReadExact(address, data))
        {
            value = 0;
            return false;
        }
        value = BinaryPrimitives.ReadInt32LittleEndian(data);
        return true;
    }

    private bool TryReadUInt64(ulong address, out ulong value)
    {
        var data = new byte[8];
        if (!TryReadExact(address, data))
        {
            value = 0;
            return false;
        }
        value = BinaryPrimitives.ReadUInt64LittleEndian(data);
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

    private static string StateName(int state) => state switch
    {
        0 => "inProgress",
        1 => "succeeded",
        2 => "failed",
        3 => "turnedIn",
        4 => "dropped",
        5 => "started",
        6 => "acquired",
        _ => "unknown",
    };

    private static string FirstNonempty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static (string Style, string Text) ReadMarker(string rawDisplay)
    {
        var match = Regex.Match(
            rawDisplay,
            "<font\\s+style\\s*=\\s*[\\\"']?(?<style>[^>\\\"'\\s]+)[\\\"']?[^>]*>(?<text>.*?)</font>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return match.Success
            ? (match.Groups["style"].Value, TranscriptRenderer.CleanText(match.Groups["text"].Value))
            : (string.Empty, string.Empty);
    }

    private static string? InferRecruitmentType(
        string missionInternalName,
        string objectiveInternalName,
        string markerStyle)
    {
        if (markerStyle.Contains("Delta", StringComparison.OrdinalIgnoreCase)) return "delta";
        if (markerStyle.Contains("Temporal", StringComparison.OrdinalIgnoreCase)) return "temporal";
        if (markerStyle.Contains("Gamma", StringComparison.OrdinalIgnoreCase)) return "gamma";
        if (markerStyle.Contains("Klingon", StringComparison.OrdinalIgnoreCase)) return "klingon";

        // Verified live and by the Helix Delta Recruit objective definition.
        if (missionInternalName.Equals("Rom_Suliban_Helix_Part_1_Om", StringComparison.OrdinalIgnoreCase) &&
            objectiveInternalName.Equals("Dr", StringComparison.OrdinalIgnoreCase) &&
            markerStyle.Equals("Mission_Recruit", StringComparison.OrdinalIgnoreCase))
        {
            return "delta";
        }
        return null;
    }

    private sealed record MissionInfoView(
        ulong MissionsAddress,
        string PrimaryMission,
        ulong CurrentOpenMissionNameAddress,
        string CurrentOpenMissionName);

    private sealed record MissionNode(
        ulong Address,
        ulong NameAddress,
        string InternalName,
        ulong ChildrenAddress,
        int State,
        bool Tracking,
        bool Hidden,
        ulong RootDefinition,
        ulong RootOverride,
        string NameOverride,
        int Count,
        int Target,
        int Depth,
        int DisplayOrder)
    {
        public string ParentInternalName { get; init; } = string.Empty;
        public int TraversalOrder { get; init; }
    }

    private sealed record MissionDefinition(
        ulong Address,
        string InternalName,
        string DisplayName,
        string UiString,
        string IconName,
        bool OmitFromTracker,
        int MissionUiType,
        IReadOnlyList<int> ObjectiveTags);
}
