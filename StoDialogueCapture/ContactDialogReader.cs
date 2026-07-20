using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace StoDialogueCapture;

internal sealed class ContactDialogReader
{
    private const int ContactDialogReadSize = 640;
    private const int MaxStringBytes = 64 * 1024;
    private const int MaxOptions = 128;
    private const int ScanChunkSize = 4 * 1024 * 1024;
    private const uint MemPrivate = 0x20000;
    private const uint MemMapped = 0x40000;

    // Heap allocators reuse the same neighborhoods, so a replacement
    // ContactDialog and its strings normally land near the previous ones.
    // Regions within this radius of any remembered address are scanned first.
    private const ulong HotRegionRadius = 64UL * 1024 * 1024;

    private static readonly int ScanParallelism = Math.Clamp(Environment.ProcessorCount, 1, 16);
    private static readonly int[] AnchorFieldOffsets = [0x08, 0x10, 0x18, 0x28, 0x30, 0x38];

    private readonly IMemoryReader _memory;
    private readonly List<MemoryRegion> _readableRegions;
    private readonly List<ulong> _recentAddresses = [];

    private readonly record struct AnchorSpec(string Value, bool AllowPrefix, string Kind);

    private readonly record struct ScanChunk(ulong Address, int ReadLength, int ReportLength);

    public ContactDialogReader(IMemoryReader memory)
    {
        _memory = memory;
        _readableRegions = [];
        RefreshRegions();
    }

    public DiscoveryResult Discover(string anchor, Action<string>? progress = null)
    {
        return DiscoverCore(
            [new AnchorSpec(anchor, AllowPrefix: false, "anchor")],
            _readableRegions,
            requireLiveDialogue: false,
            progress,
            scanLabel: "all readable memory");
    }

    public DiscoveryResult? TryReacquire(
        TranscriptChatMessage trigger,
        Action<string>? progress = null)
    {
        RefreshRegions();
        var anchors = new List<AnchorSpec>();
        if (!string.IsNullOrWhiteSpace(trigger.Speaker))
        {
            anchors.Add(new AnchorSpec(trigger.Speaker, AllowPrefix: false, "speaker"));
        }
        if (!string.IsNullOrWhiteSpace(trigger.Text))
        {
            anchors.Add(new AnchorSpec(trigger.Text, AllowPrefix: true, "spoken-text prefix"));
        }
        var distinctAnchors = anchors
            .DistinctBy(anchor => anchor.Value, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctAnchors.Count == 0)
        {
            return null;
        }

        var privateRegions = _readableRegions
            .Where(region => region.Type == MemPrivate)
            .ToList();
        var hotRegions = privateRegions.Where(IsHotRegion).ToList();
        if (hotRegions.Count > 0 && hotRegions.Count < privateRegions.Count)
        {
            try
            {
                return DiscoverCore(
                    distinctAnchors,
                    hotRegions,
                    requireLiveDialogue: true,
                    progress,
                    scanLabel: "regions near the last dialogue");
            }
            catch (InvalidOperationException)
            {
                progress?.Invoke(
                    "No replacement near the last dialogue; scanning all private memory...");
            }
        }

        try
        {
            return DiscoverCore(
                distinctAnchors,
                privateRegions,
                requireLiveDialogue: true,
                progress,
                scanLabel: "all private memory");
        }
        catch (InvalidOperationException exception)
        {
            progress?.Invoke($"Reacquisition anchors did not resolve: {exception.Message}");
            return null;
        }
    }

    private DiscoveryResult DiscoverCore(
        IReadOnlyList<AnchorSpec> anchors,
        IReadOnlyList<MemoryRegion> patternRegions,
        bool requireLiveDialogue,
        Action<string>? progress,
        string scanLabel)
    {
        if (anchors.Count == 0 || anchors.All(anchor => string.IsNullOrWhiteSpace(anchor.Value)))
        {
            throw new ArgumentException("Anchor must contain visible dialogue text.", nameof(anchors));
        }

        var stopwatch = Stopwatch.StartNew();
        var patterns = anchors
            .SelectMany(anchor => BuildAnchorPatterns(anchor.Value, anchor.AllowPrefix))
            .ToList();
        var scanBytes = patternRegions.Sum(region => (long)region.Size);
        progress?.Invoke(
            $"Scanning {patternRegions.Count:N0} region(s) ({scanBytes / (1024 * 1024):N0} MB, {scanLabel}) " +
            $"for {patterns.Count} anchor pattern(s) on {ScanParallelism} core(s)...");
        var anchorAddresses = FindPatterns(patterns, 256, patternRegions).ToHashSet();
        if (anchorAddresses.Count == 0)
        {
            throw new InvalidOperationException(
                "The anchor text was not found in readable GameClient memory. Keep the dialogue open and use an exact visible header.");
        }

        progress?.Invoke(
            $"Found {anchorAddresses.Count:N0} anchor address(es) in {stopwatch.ElapsedMilliseconds:N0} ms; " +
            "locating pointer references...");
        var candidateAddresses = new HashSet<ulong>();
        foreach (var reference in FindPointerReferences(anchorAddresses, 4096, patternRegions))
        {
            foreach (var offset in AnchorFieldOffsets)
            {
                if (reference >= (ulong)offset)
                {
                    candidateAddresses.Add(reference - (ulong)offset);
                }
            }
        }

        progress?.Invoke($"Validating {candidateAddresses.Count:N0} candidate structure address(es)...");
        var anchorValues = anchors.Select(anchor => anchor.Value).ToList();
        var candidates = candidateAddresses
            .Select(address => TryReadDialog(address, anchorValues))
            .Where(candidate => candidate != null)
            .Cast<DialogCandidate>()
            .Where(candidate => !requireLiveDialogue || IsCredibleLiveDialogue(candidate.Snapshot))
            .OrderByDescending(candidate => candidate.Score)
            .ToList();
        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "Anchor references were found, but none matched the exported ContactDialog layout.");
        }

        var best = candidates[0];
        if (candidates.Count > 1 && candidates[1].Score == best.Score &&
            candidates[1].Snapshot.DialogHeader == best.Snapshot.DialogHeader)
        {
            progress?.Invoke($"Warning: {candidates.Count} valid copies found; selecting the highest-scoring live copy.");
        }

        // The player/contact owner is heap-resident in the x64 client.  A map
        // transition can replace ContactDialog while updating a pointer in
        // that owner, so retaining only module-global references loses the
        // conversation as soon as the map changes.  Mapped file regions are
        // asset caches and cannot hold those live owner slots.
        var pointerSlots = FindPointerReferences(
            new HashSet<ulong> { best.Address },
            4096,
            PointerSlotRegions());
        var modulePointerSlotCount = pointerSlots.Count(address =>
            address >= _memory.MainModuleBase &&
            address < _memory.MainModuleBase + _memory.MainModuleSize);

        RememberScanNeighborhood(best.Address, pointerSlots, anchorAddresses);
        progress?.Invoke(
            $"Selected ContactDialog 0x{best.Address:X} (score {best.Score}) " +
            $"in {stopwatch.ElapsedMilliseconds:N0} ms; " +
            $"following {pointerSlots.Count} pointer slot(s) " +
            $"({modulePointerSlotCount} in the main module).");
        return new DiscoveryResult(best, pointerSlots, anchorAddresses.Order().ToList());
    }

    private void RefreshRegions()
    {
        _memory.RefreshRegions();
        _readableRegions.Clear();
        _readableRegions.AddRange(
            _memory.Regions.Where(region => region.IsReadable).OrderBy(region => region.BaseAddress));
    }

    private void RememberScanNeighborhood(
        ulong dialogAddress,
        IReadOnlyList<ulong> pointerSlots,
        IEnumerable<ulong> anchorAddresses)
    {
        _recentAddresses.Clear();
        _recentAddresses.Add(dialogAddress);
        _recentAddresses.AddRange(pointerSlots.Take(64));
        _recentAddresses.AddRange(anchorAddresses.Take(64));
    }

    private bool IsHotRegion(MemoryRegion region)
    {
        if (_recentAddresses.Count == 0)
        {
            return false;
        }
        var lower = region.BaseAddress >= HotRegionRadius ? region.BaseAddress - HotRegionRadius : 0;
        var upper = region.EndAddress + HotRegionRadius;
        foreach (var address in _recentAddresses)
        {
            if (address >= lower && address < upper)
            {
                return true;
            }
        }
        return false;
    }

    private List<MemoryRegion> PointerSlotRegions() =>
        _readableRegions.Where(region => region.Type != MemMapped).ToList();

    public DialogCandidate? TryReadBest(
        IEnumerable<ulong> addresses,
        string? requiredAnchor = null,
        bool requireLiveDialogue = false)
    {
        var requiredAnchors = requiredAnchor == null ? null : new[] { requiredAnchor };
        return addresses
            .Distinct()
            .Select(address => TryReadDialog(address, requiredAnchors))
            .Where(candidate => candidate != null)
            .Cast<DialogCandidate>()
            .Where(candidate => !requireLiveDialogue || IsCredibleLiveDialogue(candidate.Snapshot))
            .OrderByDescending(candidate => candidate.Score)
            .FirstOrDefault();
    }

    internal static bool IsCredibleLiveDialogue(ContactDialogSnapshot snapshot)
    {
        if (snapshot.ScreenType <= 0 || snapshot.Options.Count == 0)
        {
            return false;
        }

        var identity = string.IsNullOrWhiteSpace(snapshot.ContactDisplayName)
            ? snapshot.DialogHeader
            : snapshot.ContactDisplayName;
        if (!LooksLikeNaturalText(identity) || identity.Length > 256)
        {
            return false;
        }

        var structuralText = string.Join(' ', snapshot.DialogHeader, snapshot.DialogHeader2, snapshot.ListHeader);
        return !structuralText.Contains("Rewardrow", StringComparison.OrdinalIgnoreCase) &&
               !structuralText.Contains("Offscreenicon", StringComparison.OrdinalIgnoreCase) &&
               !structuralText.Contains("{Item", StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<ulong> ResolvePointerSlots(IEnumerable<ulong> slots)
    {
        var values = new List<ulong>();
        foreach (var slot in slots)
        {
            if (TryReadUInt64(slot, out var value) && IsPlausiblePointer(value))
            {
                values.Add(value);
            }
        }
        return values;
    }

    private DialogCandidate? TryReadDialog(ulong address, IReadOnlyList<string>? requiredAnchors)
    {
        var data = new byte[ContactDialogReadSize];
        if (!TryReadExact(address, data))
        {
            return null;
        }

        var screenType = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(0, 4));
        if (screenType is < -1 or > 256)
        {
            return null;
        }

        var contactDisplayName = ReadPointerString(data, 0x08);
        var dialogHeader = ReadPointerString(data, 0x10);
        var dialogText1 = ReadPointerString(data, 0x18);
        var dialogHeader2 = ReadPointerString(data, 0x28);
        var dialogText2 = ReadPointerString(data, 0x30);
        var listHeader = ReadPointerString(data, 0x38);
        var voicePath = ReadPointerString(data, 0x40);
        var phrasePath = ReadPointerString(data, 0x48);
        // Client-side field in the corrected x64 ContactDialog schema. The
        // newly displayed node retains the exact response that led to it.
        var lastResponseDisplayString = ReadPointerString(data, 0x200);

        var primaryStrings = new[]
        {
            contactDisplayName,
            dialogHeader,
            dialogText1,
            dialogHeader2,
            dialogText2,
            listHeader,
        };
        var nonEmptyCount = primaryStrings.Count(value => !string.IsNullOrWhiteSpace(value));
        if (nonEmptyCount < 2)
        {
            return null;
        }
        if (requiredAnchors is { Count: > 0 } &&
            !requiredAnchors.Any(anchor => primaryStrings.Any(value =>
                value.Contains(anchor, StringComparison.OrdinalIgnoreCase))))
        {
            return null;
        }

        if (!TryReadOptions(data, out var options))
        {
            return null;
        }

        var score = nonEmptyCount * 10;
        score += options.Count * 5;
        if (requiredAnchors is { Count: > 0 })
        {
            score += requiredAnchors.Max(anchor =>
                string.Equals(contactDisplayName, anchor, StringComparison.OrdinalIgnoreCase) ? 300 :
                string.Equals(dialogHeader, anchor, StringComparison.OrdinalIgnoreCase) ? 200 :
                string.Equals(dialogHeader2, anchor, StringComparison.OrdinalIgnoreCase) ? 100 :
                string.Equals(listHeader, anchor, StringComparison.OrdinalIgnoreCase) ? 75 : 50);
        }
        score += LooksLikeNaturalText(dialogHeader) ? 10 : 0;
        score += LooksLikeNaturalText(dialogText1) || LooksLikeNaturalText(dialogText2) ? 15 : 0;
        score += options.Any(option => !string.IsNullOrWhiteSpace(option.DisplayString)) ? 20 : 0;

        var snapshot = new ContactDialogSnapshot(
            DateTimeOffset.UtcNow,
            "dialogue",
            "GameClient",
            $"0x{address:X}",
            screenType,
            contactDisplayName,
            dialogHeader,
            dialogText1,
            dialogHeader2,
            dialogText2,
            listHeader,
            voicePath,
            phrasePath,
            lastResponseDisplayString,
            options);
        return new DialogCandidate(address, score, snapshot);
    }

    private bool TryReadOptions(byte[] dialogData, out IReadOnlyList<ContactDialogOptionSnapshot> options)
    {
        options = Array.Empty<ContactDialogOptionSnapshot>();
        var arrayAddress = BinaryPrimitives.ReadUInt64LittleEndian(dialogData.AsSpan(0xD8, 8));
        if (arrayAddress == 0)
        {
            return true;
        }
        if (!IsPlausiblePointer(arrayAddress) || arrayAddress < 0x10)
        {
            return false;
        }

        if (!TryReadInt32(arrayAddress - 0x10, out var count) || count is < 0 or > MaxOptions)
        {
            return false;
        }
        if (count == 0)
        {
            return true;
        }

        var pointerBytes = new byte[count * 8];
        if (!TryReadExact(arrayAddress, pointerBytes))
        {
            return false;
        }

        var parsed = new List<ContactDialogOptionSnapshot>(count);
        for (var i = 0; i < count; i++)
        {
            var optionAddress = BinaryPrimitives.ReadUInt64LittleEndian(pointerBytes.AsSpan(i * 8, 8));
            if (!IsPlausiblePointer(optionAddress))
            {
                return false;
            }
            var optionData = new byte[192];
            if (!TryReadExact(optionAddress, optionData))
            {
                return false;
            }

            var key = ReadPointerString(optionData, 0x00);
            var displayString = ReadPointerString(optionData, 0x08);
            if (string.IsNullOrEmpty(key) && string.IsNullOrEmpty(displayString))
            {
                return false;
            }

            parsed.Add(new ContactDialogOptionSnapshot(
                key,
                displayString,
                ReadPointerString(optionData, 0x10),
                BinaryPrimitives.ReadInt32LittleEndian(optionData.AsSpan(0x20, 4)),
                optionData[0x24] != 0,
                ReadPointerString(optionData, 0x28),
                ReadPointerString(optionData, 0x30),
                optionData[0x8C] != 0,
                BinaryPrimitives.ReadInt32LittleEndian(optionData.AsSpan(0x94, 4)),
                optionData[0xA0] != 0,
                optionData[0xAC] != 0,
                optionData[0xAD] != 0,
                optionData[0xAE] != 0,
                optionData[0xAF] != 0,
                optionData[0xB0] != 0,
                ReadPointerString(optionData, 0xB8)));
        }
        options = parsed;
        return true;
    }

    private string ReadPointerString(byte[] container, int offset)
    {
        var pointer = BinaryPrimitives.ReadUInt64LittleEndian(container.AsSpan(offset, 8));
        return TryReadCString(pointer, out var value) ? value : string.Empty;
    }

    private bool TryReadCString(ulong address, out string value)
    {
        value = string.Empty;
        if (address == 0)
        {
            return true;
        }
        if (!IsPlausiblePointer(address))
        {
            return false;
        }

        using var stream = new MemoryStream();
        var buffer = new byte[256];
        var cursor = address;
        while (stream.Length < MaxStringBytes)
        {
            if (!_memory.TryRead(cursor, buffer, 0, buffer.Length, out var read) || read <= 0)
            {
                return false;
            }
            var end = Array.IndexOf(buffer, (byte)0, 0, read);
            if (end >= 0)
            {
                stream.Write(buffer, 0, end);
                try
                {
                    value = new UTF8Encoding(false, true).GetString(stream.ToArray());
                }
                catch (DecoderFallbackException)
                {
                    return false;
                }
                return IsReasonableString(value);
            }
            stream.Write(buffer, 0, read);
            cursor += (ulong)read;
        }
        return false;
    }

    private static List<ScanChunk> BuildChunks(IReadOnlyList<MemoryRegion> regions, int overlap)
    {
        var chunks = new List<ScanChunk>();
        foreach (var region in regions)
        {
            ulong offset = 0;
            while (offset < region.Size)
            {
                var report = (int)Math.Min((ulong)ScanChunkSize, region.Size - offset);
                var read = (int)Math.Min((ulong)report + (ulong)overlap, region.Size - offset);
                chunks.Add(new ScanChunk(region.BaseAddress + offset, read, report));
                offset += (ulong)report;
            }
        }
        return chunks;
    }

    private List<ulong> FindPointerReferences(
        IReadOnlySet<ulong> pointers,
        int maximumMatches,
        IReadOnlyList<MemoryRegion> regions)
    {
        if (pointers.Count == 0)
        {
            return [];
        }

        var chunks = BuildChunks(regions, 0);
        var matches = new ConcurrentBag<ulong>();
        var found = 0;
        Parallel.ForEach(
            chunks,
            new ParallelOptions { MaxDegreeOfParallelism = ScanParallelism },
            (chunk, state) =>
            {
                if (Volatile.Read(ref found) >= maximumMatches)
                {
                    state.Stop();
                    return;
                }
                var buffer = ArrayPool<byte>.Shared.Rent(chunk.ReadLength);
                try
                {
                    if (!_memory.TryRead(chunk.Address, buffer, 0, chunk.ReadLength, out var read) || read < 8)
                    {
                        return;
                    }
                    var alignment = (int)((8 - (chunk.Address & 7)) & 7);
                    for (var offset = alignment; offset <= read - 8; offset += 8)
                    {
                        var value = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, 8));
                        if (!pointers.Contains(value))
                        {
                            continue;
                        }
                        matches.Add(chunk.Address + (ulong)offset);
                        if (Interlocked.Increment(ref found) >= maximumMatches)
                        {
                            state.Stop();
                            return;
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            });
        return matches.Distinct().Order().ToList();
    }

    private List<ulong> FindPatterns(
        IReadOnlyList<byte[]> patterns,
        int maximumMatches,
        IReadOnlyList<MemoryRegion> regions)
    {
        if (patterns.Count == 0)
        {
            return [];
        }

        var overlap = Math.Max(0, patterns.Max(pattern => pattern.Length) - 1);
        var chunks = BuildChunks(regions, overlap);
        var matches = new ConcurrentBag<ulong>();
        var found = 0;
        Parallel.ForEach(
            chunks,
            new ParallelOptions { MaxDegreeOfParallelism = ScanParallelism },
            (chunk, state) =>
            {
                if (Volatile.Read(ref found) >= maximumMatches)
                {
                    state.Stop();
                    return;
                }
                var buffer = ArrayPool<byte>.Shared.Rent(chunk.ReadLength);
                try
                {
                    if (!_memory.TryRead(chunk.Address, buffer, 0, chunk.ReadLength, out var read) || read <= 0)
                    {
                        return;
                    }
                    var span = buffer.AsSpan(0, Math.Min(read, chunk.ReadLength));
                    foreach (var pattern in patterns)
                    {
                        if (pattern.Length == 0 || span.Length < pattern.Length)
                        {
                            continue;
                        }
                        var searchStart = 0;
                        while (searchStart <= span.Length - pattern.Length)
                        {
                            var index = span[searchStart..].IndexOf(pattern);
                            if (index < 0)
                            {
                                break;
                            }
                            index += searchStart;
                            if (index >= chunk.ReportLength)
                            {
                                break;
                            }
                            matches.Add(chunk.Address + (ulong)index);
                            if (Interlocked.Increment(ref found) >= maximumMatches)
                            {
                                state.Stop();
                                return;
                            }
                            searchStart = index + 1;
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            });
        return matches.Distinct().Order().ToList();
    }

    private static IEnumerable<byte[]> BuildAnchorPatterns(string anchor, bool allowPrefix)
    {
        if (allowPrefix)
        {
            yield return Encoding.UTF8.GetBytes(anchor);
            yield return Encoding.Unicode.GetBytes(anchor);
        }
        else
        {
            yield return Encoding.UTF8.GetBytes(anchor + "\0");
            yield return Encoding.Unicode.GetBytes(anchor + "\0");
        }
    }

    private bool TryReadExact(ulong address, byte[] buffer)
    {
        return _memory.TryRead(address, buffer, 0, buffer.Length, out var read) && read == buffer.Length;
    }

    private bool TryReadUInt64(ulong address, out ulong value)
    {
        var buffer = new byte[8];
        if (!TryReadExact(address, buffer))
        {
            value = 0;
            return false;
        }
        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    private bool TryReadInt32(ulong address, out int value)
    {
        var buffer = new byte[4];
        if (!TryReadExact(address, buffer))
        {
            value = 0;
            return false;
        }
        value = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        return true;
    }

    private bool IsPlausiblePointer(ulong address)
    {
        if (address < 0x10000 || address > 0x00007FFFFFFFFFFF)
        {
            return false;
        }
        var lo = 0;
        var hi = _readableRegions.Count - 1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var region = _readableRegions[mid];
            if (address < region.BaseAddress)
            {
                hi = mid - 1;
            }
            else if (address >= region.EndAddress)
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsReasonableString(string value)
    {
        if (value.Length > MaxStringBytes)
        {
            return false;
        }
        var badControls = value.Count(ch => char.IsControl(ch) && ch is not '\r' and not '\n' and not '\t');
        return badControls == 0;
    }

    private static bool LooksLikeNaturalText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        var letters = value.Count(char.IsLetter);
        return letters >= 3 && letters * 2 >= Math.Min(value.Length, 40);
    }
}
