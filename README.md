# STO dialogue capture sidecar

`StoDialogueCapture` records Star Trek Online mission dialogue as you play and
renders it into a clean, wiki-ready Markdown transcript. It is a read-only,
out-of-process observer: no DLL injection, no remote threads, no breakpoints,
no code patching, and no process-write access. It never sends commands to the
game.

Each capture produces two files:

- **`*.jsonl`** — the lossless audit record: every dialogue window, choice,
  selected response, mission update, ambient NPC line, and objective snapshot.
- **`*.transcript.md`** — a cleaned, human-readable transcript with speaker
  headings, full dialogue text, **Choices shown** (selected option marked),
  **Arrived via choice** transitions, and mission-progress checklists.

## Quick start (GUI)

1. Start STO and type `/ChatLog 1` in chat once (enables the game's own chat
   log; the sidecar only reads it).
2. Launch `dist\StoDialogueCapture.Gui.exe`.
3. Open the mission's first dialogue in game, put its exact visible name
   (e.g. `Mission Simulator`) in the Anchor field, and press **Start capture**.
4. Play the mission. When finished, press **Stop and create transcript**.

## Quick start (CLI)

```powershell
& '.\dist\StoDialogueCapture.exe' `
  --anchor 'Mission Simulator' `
  --output '.\dialogue.jsonl'
```

Press Ctrl+C to stop; the transcript is rendered next to the JSONL. Useful
options: `--transcript PATH`, `--no-transcript`, `--chat-log PATH`,
`--no-chat-log`, and `--render FILE.jsonl` to re-render an existing capture
without the game running.

If `OpenProcess` reports access denied, run the same command from an elevated
PowerShell. The requested rights remain query/read only.

## Build

From the repository root (requires the .NET 8 SDK):

```powershell
dotnet build StoDialogueCapture\StoDialogueCapture.csproj -c Release
dotnet build StoDialogueCapture.Gui\StoDialogueCapture.Gui.csproj -c Release
```

Single-file executables for `dist\`:

```powershell
dotnet publish StoDialogueCapture\StoDialogueCapture.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
dotnet publish StoDialogueCapture.Gui\StoDialogueCapture.Gui.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist
```

Verify a build with the synthetic self-test (no game needed):

```powershell
& '.\dist\StoDialogueCapture.exe' --self-test
```

## How it works

The tool scans the game client's readable memory for the anchor text, resolves
pointer references to locate the live `ContactDialog` structure, and then polls
it (250 ms default), appending a JSON line whenever the header, body, or option
set changes. The client-side last-response field links each new node to the
choice that led to it, so transcripts show the route actually played; choices
that were never selected have no known destination until another capture plays
them.

When a dialogue closes (for example on a map transition), the sidecar watches
retained owner pointer slots and the chat log's `[NPC]` lines to find the
replacement dialogue, then resumes automatically. Scans are chunked, run in
parallel across CPU cores, and check the previous dialogue's heap neighborhood
first, so reacquisition normally completes in well under a second. Repeated
bridge-officer combat callouts are attempted as triggers at most once per
closed dialogue.

The chat log tailer also merges `[Mission]` fly-in messages and `[NPC]` ambient
speech into the capture; other channels (player chat, combat, `[System]`) are
ignored. The transcript renderer removes markup, exact duplicates, and NPC
excerpts that duplicate a full dialogue window — nothing is ever deleted from
the source JSONL.

Field offsets come from a locally exported, corrected x64 schema set for the
live client; the original 32-bit offsets are not compatible.
