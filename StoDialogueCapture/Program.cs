namespace StoDialogueCapture;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--probe-current-mission")
            {
                if (args.Length != 1 &&
                    (args.Length != 3 || args[1] != "--append-jsonl"))
                {
                    throw new ArgumentException(
                        "Usage: --probe-current-mission [--append-jsonl PATH]");
                }
                using var memory = ProcessMemory.Open("GameClient");
                var reader = new MissionProgressReader(memory);
                var snapshot = reader.TryRead("GameClient", Console.WriteLine);
                if (snapshot == null)
                {
                    Console.WriteLine("No current open-mission progress snapshot was available.");
                }
                else
                {
                    var jsonOptions = new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                        WriteIndented = args.Length == 1,
                    };
                    var json = System.Text.Json.JsonSerializer.Serialize(snapshot, jsonOptions);
                    if (args.Length == 3)
                    {
                        File.AppendAllText(
                            Path.GetFullPath(args[2]),
                            json + Environment.NewLine,
                            new System.Text.UTF8Encoding(false));
                        Console.WriteLine($"Appended current mission progress to {Path.GetFullPath(args[2])}");
                    }
                    else
                    {
                        Console.WriteLine(json);
                    }
                }
                return snapshot == null ? 2 : 0;
            }

            if (args.Length > 0 && args[0] == "--probe-player-missions")
            {
                if (args.Length != 2)
                {
                    throw new ArgumentException("Usage: --probe-player-missions \"PLAYER NAME\"");
                }
                using var memory = ProcessMemory.Open("GameClient");
                Console.WriteLine(
                    $"Attached read-only to GameClient; module 0x{memory.MainModuleBase:X}-" +
                    $"0x{memory.MainModuleBase + memory.MainModuleSize:X}.");
                MissionTrackerProbe.RunPlayer(memory, args[1], Console.WriteLine);
                return 0;
            }

            if (args.Length > 0 && args[0] == "--probe-mission-tracker")
            {
                if (args.Length is < 3 or > 4)
                {
                    throw new ArgumentException(
                        "Usage: --probe-mission-tracker \"MISSION TITLE\" \"VISIBLE OBJECTIVE\" [PLAYER NAME]");
                }
                using var memory = ProcessMemory.Open("GameClient");
                Console.WriteLine(
                    $"Attached read-only to GameClient; module 0x{memory.MainModuleBase:X}-" +
                    $"0x{memory.MainModuleBase + memory.MainModuleSize:X}.");
                MissionTrackerProbe.Run(memory, args[1], args[2], args.Length == 4 ? args[3] : null, Console.WriteLine);
                return 0;
            }

            var options = CaptureOptions.Parse(args);
            if (options.ShowHelp)
            {
                CaptureOptions.PrintHelp();
                return 0;
            }
            if (options.SelfTest)
            {
                SelfTests.Run();
                Console.WriteLine("All self-tests passed.");
                return 0;
            }
            if (options.RenderInputPath != null)
            {
                PrintRenderResult(TranscriptRenderer.Render(options.RenderInputPath, options.TranscriptPath));
                return 0;
            }

            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                Console.WriteLine("Press Ctrl+C to stop.");
                var result = await CaptureSession.RunAsync(
                    options,
                    update => Console.WriteLine(update.Message),
                    cancellation.Token);
                if (result.Transcript != null)
                {
                    PrintRenderResult(result.Transcript);
                }
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ERROR: {exception.Message}");
            if (Environment.GetEnvironmentVariable("STO_CAPTURE_VERBOSE") == "1")
            {
                Console.Error.WriteLine(exception);
            }
            return 1;
        }
    }

    private static void PrintRenderResult(TranscriptRenderResult result)
    {
        Console.WriteLine($"Human-readable transcript written to {result.OutputPath}");
        Console.WriteLine(
            $"Transcript contains {result.DialogueCount} dialogue window(s), " +
            $"{result.MissionMessageCount} mission message(s), and " +
            $"{result.NpcMessageCount} standalone NPC message(s), with " +
            $"{result.MissionProgressCount} mission-progress snapshot(s). " +
            $"Omitted {result.SuppressedCount} duplicate/overlapping artifact(s).");
        if (result.MalformedLineCount > 0)
        {
            Console.WriteLine($"Warning: skipped {result.MalformedLineCount} malformed JSONL line(s).");
        }
    }
}

internal sealed record CaptureOptions(
    string ProcessName,
    string? Anchor,
    string OutputPath,
    int PollMilliseconds,
    bool CaptureMissionChat,
    string? ChatLogPath,
    string? RenderInputPath,
    string? TranscriptPath,
    bool GenerateTranscript,
    bool SelfTest,
    bool ShowHelp)
{
    public static CaptureOptions Parse(string[] args)
    {
        var processName = "GameClient";
        string? anchor = null;
        var output = "sto-dialogue-capture.jsonl";
        var poll = 250;
        var captureMissionChat = true;
        string? chatLogPath = null;
        string? renderInputPath = null;
        string? transcriptPath = null;
        var generateTranscript = true;
        var selfTest = false;
        var showHelp = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--process":
                    processName = RequireValue(args, ref i);
                    break;
                case "--anchor":
                    anchor = RequireValue(args, ref i);
                    break;
                case "--output":
                    output = RequireValue(args, ref i);
                    break;
                case "--poll-ms":
                    poll = int.Parse(RequireValue(args, ref i));
                    if (poll is < 50 or > 60_000)
                    {
                        throw new ArgumentOutOfRangeException(nameof(args), "--poll-ms must be between 50 and 60000.");
                    }
                    break;
                case "--chat-log":
                    chatLogPath = RequireValue(args, ref i);
                    captureMissionChat = true;
                    break;
                case "--no-chat-log":
                    captureMissionChat = false;
                    break;
                case "--render":
                    renderInputPath = RequireValue(args, ref i);
                    break;
                case "--transcript":
                    transcriptPath = RequireValue(args, ref i);
                    break;
                case "--no-transcript":
                    generateTranscript = false;
                    break;
                case "--self-test":
                    selfTest = true;
                    break;
                case "-h":
                case "--help":
                    showHelp = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (!selfTest && !showHelp && renderInputPath == null && string.IsNullOrWhiteSpace(anchor))
        {
            throw new ArgumentException("--anchor is required. Use an exact visible dialogue header.");
        }
        return new CaptureOptions(
            processName,
            anchor,
            output,
            poll,
            captureMissionChat,
            chatLogPath,
            renderInputPath,
            transcriptPath,
            generateTranscript,
            selfTest,
            showHelp);
    }

    private static string RequireValue(string[] args, ref int index)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"Missing value after {args[index - 1]}.");
        }
        return args[index];
    }

    public static void PrintHelp()
    {
        Console.WriteLine("STO read-only dialogue capture sidecar");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  StoDialogueCapture --anchor \"Mission Simulator\" [options]");
        Console.WriteLine("  StoDialogueCapture --render capture.jsonl [--transcript output.md]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --anchor TEXT       Exact visible header used for first discovery (required)");
        Console.WriteLine("  --output PATH       JSONL output (default: sto-dialogue-capture.jsonl)");
        Console.WriteLine("  --process NAME      Process name (default: GameClient)");
        Console.WriteLine("  --poll-ms N         Poll interval, 50-60000 (default: 250)");
        Console.WriteLine("  --chat-log PATH     Override STO chat.log path");
        Console.WriteLine("  --no-chat-log       Do not merge [Mission]/[NPC] chat-log entries");
        Console.WriteLine("  --transcript PATH   Markdown transcript path (default: OUTPUT.transcript.md)");
        Console.WriteLine("  --no-transcript     Do not render Markdown when capture stops");
        Console.WriteLine("  --render PATH       Render an existing JSONL capture and exit");
        Console.WriteLine("  --self-test         Run synthetic-memory tests and exit");
        Console.WriteLine("  -h, --help          Show help");
    }
}
