# STO dialogue capture sidecar

`StoDialogueCapture` is a read-only, out-of-process observer for the live
`ContactDialog` object in `GameClient.exe`. It does not inject a DLL, create a
remote thread, set breakpoints, patch code, or request process-write access.
It also tails STO's own `chat.log` and merges only `[Mission]` fly-in messages
and `[NPC]` ambient speech into the same JSONL output.

When capture stops normally with Ctrl+C, the sidecar also renders a cleaned,
human-readable Markdown transcript next to the JSONL file. The JSONL remains
the lossless technical/audit record.

## Windows interface (recommended)

Launch the packaged Windows application:

```text
C:\STO2\Research\RuntimeCapture\dist\StoDialogueCapture.Gui.exe
```

The interface shows whether `GameClient` and its chat log are available,
provides the initial speaker/header and JSONL destination fields, and displays
scanning, capture, closure, and reacquisition activity live. Use **Start
capture** after opening the first dialogue. Use **Stop and create transcript**
after the mission; this is the UI equivalent of Ctrl+C and creates the Markdown
transcript before enabling the Open buttons.

**Render existing JSONL** creates or refreshes a readable transcript without a
running game. Advanced fields retain the CLI defaults (`GameClient`, 250 ms),
and the output defaults to a timestamped file under Documents\STO Dialogue
Captures so sessions are not accidentally appended together.

The original terminal executable remains available for scripted use and for
users who prefer PowerShell.

## Build

```powershell
dotnet build C:\STO2\Research\RuntimeCapture\StoDialogueCapture\StoDialogueCapture.csproj -c Release
dotnet build C:\STO2\Research\RuntimeCapture\StoDialogueCapture.Gui\StoDialogueCapture.Gui.csproj -c Release
```

A tested single-file x64 release is available at:

```text
C:\STO2\Research\RuntimeCapture\dist\StoDialogueCapture.exe
C:\STO2\Research\RuntimeCapture\dist\StoDialogueCapture.Gui.exe
```

## First discovery and capture

1. Start STO and type `/ChatLog 1` in chat once. This enables STO's built-in
   `Live\logs\GameClient\chat_YYYY-MM-DD_00-00-00.log` file; the sidecar never sends commands to the
   game itself.
2. Open the mission's first contact dialogue.
3. Copy its exact visible name, such as `Mission Simulator`.
4. Run:

```powershell
& 'C:\STO2\Research\RuntimeCapture\dist\StoDialogueCapture.exe' `
  --anchor 'Mission Simulator' `
  --output 'C:\STO2\Research\RuntimeCapture\dialogue.jsonl'
```

The tool scans readable memory once, validates candidate structures using the
exported x64 field offsets, then polls the selected object and every direct
pointer slot that referenced it during discovery. Heap pointer slots are kept
because the client can replace `ContactDialog` during a map transition while
updating its owning player/contact object. It appends one JSON line only when
the header, body, or option set changes. Press Ctrl+C to stop. By default,
`dialogue.jsonl` then produces `dialogue.transcript.md`. Use
`--transcript PATH` to choose another Markdown path, or `--no-transcript` to
keep only JSONL.

The readable transcript includes full dialogue-window text and visible choices,
Mission messages, standalone NPC speech, and changed right-side mission-progress
snapshots. Each progress section lists the exact localized tracker title and
objectives in stable first-seen mission order, with completed/active state and
count/target values. Tracker rows are rendered flat: the runtime Mission depth
describes Cryptic's mission graph and is not treated as proven HUD indentation.
The Markdown's overall mission title and each tracker section are intentionally
separate: an episode such as `The Helix` can contain a space/ground section named
`Runaway Helix`, and later sections can have independent objective histories.
The raw JSONL also retains internal names, numeric state, hidden/omit flags,
mission UI type, icon name, objective tags, parent internal name, and preorder
traversal position. Marker shapes are not guessed:
for example, a Delta Recruit triangle is preserved as raw metadata rather than
being mislabeled as an optional objective. It removes Cryptic markup, exact
duplicate snapshots, invalid resource-shaped candidates, and NPC excerpts that
duplicate part of a full dialogue window. These records are never deleted from
the source JSONL. If an older capture followed a known `Rewardrow` text
container instead of the real dialog, the renderer can recover its complete
body and infer its speaker from the nearby NPC record; it labels that section
as incomplete because the visible choices were not available.

STO sometimes writes one logical `[Mission]` or `[NPC]` chat record across
multiple physical log lines. The tailer holds the newest record through one
quiet poll and joins unprefixed continuation lines—including intentional blank
lines—before writing JSONL. On multi-map episodes, the progress reader treats a
primary-mission child as a temporary section gate and yields as soon as
`MissionInfo` advertises a different OpenMission, keeping space, ground, and
later objective histories separate.

The client-side `ContactDialog.LastResponseDisplayString` field is captured for
each changed node. When it matches a choice offered by the preceding node, the
JSONL receives a `dialogueTransition` record containing stable source/target
node IDs and the selected response. Markdown marks the selected item under
**Choices shown** and labels the next node with **Arrived via choice**. A
meaningful revisit such as `information -> Back -> original menu` is preserved;
only consecutive identical capture snapshots are collapsed. When a dialogue has
exactly one visible enabled choice, the readable transcript can safely mark it
selected even if STO closes the node before its last-response field is sampled.

Transitions describe the route actually played. Choices that were displayed
but never selected have no known destination until that alternative is played
in another capture.

An existing capture can be rendered without launching STO:

```powershell
& 'C:\STO2\Research\RuntimeCapture\dist\StoDialogueCapture.exe' `
  --render 'C:\STO2\Research\RuntimeCapture\dialogue.jsonl'
```

Add `--transcript 'C:\path\mission.md'` to override the destination.

If those direct references do not survive a map transition, the tool emits a
`closed` record and waits for the next `NPC` chat entry. It refreshes the
process memory map, extracts the speaker from STO's metadata (for example,
`Tovan Khev@`), and searches private heap memory for a replacement
`ContactDialog`. Spoken text is used as a prefix fallback when no speaker is
available. On success it emits a `reacquired` record, replaces the address and
pointer-slot set, and resumes full-window capture automatically.

While waiting, retained owner pointer slots are also checked for a replacement
dialogue. A replacement must have a live screen type, a natural contact name,
and at least one valid option, and its fingerprint must differ from the object
that just closed. This fast path normally follows conversations within one map
without performing another whole-memory anchor scan. Speaker/text scanning is
the fallback when owner pointers do not survive a map transition.

NPC entries observed before `closed` are discarded as reacquisition triggers,
and the dead address is quarantined until a new chat entry resolves a validated
replacement. This prevents retained dialogue copies from alternating between
false open/closed states. Anchor references are resolved in one batched aligned
pointer scan rather than one whole-memory pass per string copy.

By default it automatically follows
the newest `Live\logs\GameClient\chat_*.log`, starting at the file's current end
and following a replacement file if STO rotates it. Future
`[Mission]` lines become `missionMessage` records. `[NPC] Speaker: text` lines
become `npcMessage` records with separate `speaker` and `text` fields. Other
channels such as `[System]`, player chat, and combat messages are ignored. If
the file does not exist yet, the sidecar waits for it and prints the
`/ChatLog 1` reminder.

Use `--chat-log PATH` for a nonstandard location or `--no-chat-log` to disable
this second input channel.

The x64 offsets come from the corrected schemas under
`C:\STO\Tools\configs\schemas\Star Trek Online\Live\parses`. Do not substitute
the original 32-bit offsets from the uncorrected schema set.

If `OpenProcess` reports access denied, close the tool and run the same command
from an elevated PowerShell. The requested rights remain query/read only.

## Verification

```powershell
& 'C:\STO2\Research\RuntimeCapture\dist\StoDialogueCapture.exe' --self-test
```

The synthetic test covers anchor discovery, pointer-reference resolution,
`ContactDialog` offsets, the Cryptic e-array count at `array - 0x10`, and option
key/display extraction. It also verifies `[Mission]` parsing, `[NPC]` speaker
splitting, rejection of a neighboring `[System]` entry, and replacement-dialog
reacquisition using both speaker and spoken-text-prefix triggers. Transcript
tests cover markup cleanup, title inference, dialogue deduplication, overlapping
NPC excerpt removal, ambient NPC retention, visible choices, and invalid
candidate filtering. They also cover selected-response extraction, stable
transition IDs, Back navigation, selected-choice annotations, and preservation
of a meaningful dialogue revisit. Mission-progress rendering tests verify exact
HUD labels, display order, and active/completed markers. The live reader was
calibrated against `Runaway Helix`: `Answer Incoming Hail` changed from
`InProgress` to `Succeeded` while `Defeat the I.R.W. Sharrdar` appeared as the
new `InProgress` objective. When an OpenMission succeeds, the reader also
follows the surrounding tracked primary mission so wrapper steps such as
`Return to Sector Space` are not lost when the open-mission pointer disappears.

## Live verification

The packaged binary was verified against an open `Mission Simulator` window in
the x64 Steam client. It captured the `The Helix` header, both body/objective
fields, and these live choices:

```text
ViewOfferedMission.Accept -> Accept
ViewOfferedMission.Back   -> Decline (default-back option)
```

The proof record is `C:\STO2\Research\RuntimeCapture\live-options.jsonl`.
