using System.Globalization;
using System.Text;

namespace StoDialogueCapture;

internal sealed class TranscriptChatLogTailer
{
    private const string MissionMarker = "[Mission]";
    private const string NpcMarker = "[NPC]";

    private readonly string? _explicitPath;
    private readonly string? _autoDirectory;
    private readonly string _processName;
    private string? _activePath;
    private long _position;
    private string _pendingLine = string.Empty;
    private TranscriptChatMessage? _pendingMessage;
    private bool _announcedAvailable;

    private TranscriptChatLogTailer(
        string? explicitPath,
        string? autoDirectory,
        string processName,
        Action<string>? progress)
    {
        _explicitPath = explicitPath == null ? null : Path.GetFullPath(explicitPath);
        _autoDirectory = autoDirectory == null ? null : Path.GetFullPath(autoDirectory);
        _processName = processName;
        _activePath = ResolvePath();
        if (_activePath != null)
        {
            _position = new FileInfo(_activePath).Length;
            _announcedAvailable = true;
            progress?.Invoke($"Following new [Mission]/[NPC] entries in {_activePath}");
        }
        else
        {
            progress?.Invoke(
                $"No chat_*.log found in {_autoDirectory ?? Path.GetDirectoryName(_explicitPath)}. " +
                "Type /ChatLog 1 in STO; [Mission]/[NPC] capture will begin when the file appears.");
        }
    }

    public static TranscriptChatLogTailer CreateAuto(
        string processPath,
        string processName,
        Action<string>? progress = null)
    {
        var moduleDirectory = Path.GetDirectoryName(processPath)
            ?? throw new InvalidOperationException("GameClient path has no directory.");
        var directoryName = Path.GetFileName(moduleDirectory);
        var liveDirectory = directoryName.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
                            directoryName.Equals("x86", StringComparison.OrdinalIgnoreCase)
            ? Directory.GetParent(moduleDirectory)?.FullName
            : moduleDirectory;
        if (liveDirectory == null)
        {
            throw new InvalidOperationException("Could not resolve STO's Live directory.");
        }
        return new TranscriptChatLogTailer(
            null,
            Path.Combine(liveDirectory, "logs", "GameClient"),
            processName,
            progress);
    }

    public static TranscriptChatLogTailer Create(
        string path,
        string processName,
        Action<string>? progress = null) =>
        new(path, null, processName, progress);

    public IReadOnlyList<TranscriptChatMessage> ReadNewMessages(Action<string>? progress = null)
    {
        var path = ResolvePath();
        if (path == null)
        {
            return FlushPendingMessage() is { } pending
                ? [pending]
                : Array.Empty<TranscriptChatMessage>();
        }

        if (!string.Equals(path, _activePath, StringComparison.OrdinalIgnoreCase))
        {
            _activePath = path;
            _position = 0;
            _pendingLine = string.Empty;
            _pendingMessage = null;
            _announcedAvailable = false;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < _position)
            {
                _position = 0;
                _pendingLine = string.Empty;
                _pendingMessage = null;
            }

            if (!_announcedAvailable)
            {
                _announcedAvailable = true;
                progress?.Invoke($"Chat log appeared; following new [Mission]/[NPC] entries in {path}");
            }
            if (stream.Length == _position)
            {
                return FlushPendingMessage() is { } pending
                    ? [pending]
                    : Array.Empty<TranscriptChatMessage>();
            }

            stream.Position = _position;
            using var appended = new MemoryStream();
            stream.CopyTo(appended);
            _position = stream.Position;

            var text = Encoding.UTF8.GetString(appended.GetBuffer(), 0, checked((int)appended.Length));
            var combined = _pendingLine + text;
            var lines = combined.Split('\n');
            var hasTerminatingNewline = combined.EndsWith('\n');
            _pendingLine = hasTerminatingNewline ? string.Empty : lines[^1];

            var completeLineCount = hasTerminatingNewline ? lines.Length : lines.Length - 1;
            var messages = new List<TranscriptChatMessage>();
            for (var i = 0; i < completeLineCount; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (HasStructuredLogHeader(line))
                {
                    AddPendingMessage(messages);
                    _pendingMessage = ParseTranscriptLine(line, _processName);
                }
                else if (_pendingMessage != null)
                {
                    _pendingMessage = _pendingMessage with
                    {
                        Text = _pendingMessage.Text + "\n" + line,
                        RawLine = _pendingMessage.RawLine + "\n" + line,
                    };
                }
                else
                {
                    // Preserve support for older non-CSV chat formats whose
                    // [Mission]/[NPC] marker can appear after a timestamp.
                    _pendingMessage = ParseTranscriptLine(line, _processName);
                }
            }
            return messages;
        }
        catch (IOException exception)
        {
            progress?.Invoke($"Chat log read deferred: {exception.Message}");
            return Array.Empty<TranscriptChatMessage>();
        }
    }

    public TranscriptChatMessage? FlushPendingMessage()
    {
        if (_pendingMessage == null) return null;
        var pending = _pendingMessage with
        {
            Text = _pendingMessage.Text.Trim(),
            RawLine = _pendingMessage.RawLine.TrimEnd('\r', '\n'),
        };
        _pendingMessage = null;
        return pending;
    }

    private void AddPendingMessage(List<TranscriptChatMessage> messages)
    {
        if (FlushPendingMessage() is { } pending)
        {
            messages.Add(pending);
        }
    }

    private static bool HasStructuredLogHeader(string line)
    {
        if (!line.StartsWith('[')) return false;
        var closingBracket = line.IndexOf(']');
        if (closingBracket <= 1) return false;
        var header = line[1..closingBracket];
        return header.Contains(',') ||
               line.Contains(MissionMarker, StringComparison.OrdinalIgnoreCase) ||
               line.Contains(NpcMarker, StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolvePath()
    {
        if (_explicitPath != null)
        {
            return File.Exists(_explicitPath) ? _explicitPath : null;
        }
        if (_autoDirectory == null || !Directory.Exists(_autoDirectory))
        {
            return null;
        }

        var dated = Directory.EnumerateFiles(_autoDirectory, "chat_*.log")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();
        if (dated != null)
        {
            return dated.FullName;
        }

        var legacy = Path.Combine(_autoDirectory, "chat.log");
        return File.Exists(legacy) ? legacy : null;
    }

    internal static TranscriptChatMessage? ParseTranscriptLine(
        string line,
        string processName = "GameClient")
    {
        var structured = ParseStructuredHeader(line);
        if (structured != null)
        {
            return CreateMessage(
                structured.Value.Channel,
                structured.Value.Text,
                line,
                processName,
                structured.Value.Timestamp,
                structured.Value.Speaker);
        }

        var missionIndex = line.IndexOf(MissionMarker, StringComparison.OrdinalIgnoreCase);
        var npcIndex = line.IndexOf(NpcMarker, StringComparison.OrdinalIgnoreCase);
        var isMission = missionIndex >= 0 && (npcIndex < 0 || missionIndex < npcIndex);
        var markerIndex = isMission ? missionIndex : npcIndex;
        if (markerIndex < 0)
        {
            return null;
        }
        var marker = isMission ? MissionMarker : NpcMarker;
        var messageText = line[(markerIndex + marker.Length)..].Trim();
        return CreateMessage(
            isMission ? "Mission" : "NPC",
            messageText,
            line,
            processName,
            DateTimeOffset.UtcNow,
            null);
    }

    private static (string Channel, string Text, DateTimeOffset Timestamp, string? Speaker)?
        ParseStructuredHeader(string line)
    {
        if (!line.StartsWith('['))
        {
            return null;
        }
        var closingBracket = line.IndexOf(']');
        if (closingBracket <= 1)
        {
            return null;
        }
        var fields = line[1..closingBracket].Split(',');
        if (fields.Length < 2)
        {
            return null;
        }

        var channel = fields[^1].Trim();
        if (!channel.Equals("Mission", StringComparison.OrdinalIgnoreCase) &&
            !channel.Equals("NPC", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var timestamp = DateTimeOffset.UtcNow;
        if (DateTime.TryParseExact(
                fields[1],
                "yyyyMMdd'T'HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var parsedTimestamp))
        {
            timestamp = new DateTimeOffset(parsedTimestamp);
        }
        var speaker = fields.Length > 3 ? NormalizeSpeaker(fields[3]) : null;
        return (channel, line[(closingBracket + 1)..].Trim(), timestamp, speaker);
    }

    private static TranscriptChatMessage? CreateMessage(
        string channel,
        string messageText,
        string rawLine,
        string processName,
        DateTimeOffset timestamp,
        string? speakerHint)
    {
        if (messageText.Length == 0)
        {
            return null;
        }

        var speaker = speakerHint;
        var isMission = channel.Equals("Mission", StringComparison.OrdinalIgnoreCase);
        if (!isMission && string.IsNullOrWhiteSpace(speaker))
        {
            var separator = messageText.IndexOf(':');
            if (separator > 0)
            {
                speaker = messageText[..separator].Trim();
                messageText = messageText[(separator + 1)..].Trim();
            }
        }

        return new TranscriptChatMessage(
            timestamp,
            isMission ? "missionMessage" : "npcMessage",
            processName,
            isMission ? "Mission" : "NPC",
            speaker,
            messageText,
            rawLine);
    }

    private static string? NormalizeSpeaker(string value)
    {
        var speaker = value.Trim();
        var accountSeparator = speaker.IndexOf('@');
        if (accountSeparator >= 0)
        {
            speaker = speaker[..accountSeparator].Trim();
        }
        return speaker.Length == 0 ? null : speaker;
    }
}
