# GUI improvement checklist

Working checklist for StoDialogueCapture.Gui. Raw ideas came out of the
2026-07-20 session; the approved first batch is the unchecked items under
"Approved". Check items off as they land.

## Approved (build now)

- [x] **Compact always-on-top capture overlay**
  - [x] Pinned (`TopMost`) mini-window toggled from the main form; draggable
        borderless card that survives next to the STO window
  - [x] Status beacon with plain-language states:
        green "Dialogue locked — safe to click through",
        amber "Waiting for next dialogue", teal "Scanning...",
        gray "Idle / not capturing"
  - [x] "Last captured" line: speaker + first line of body + arrived-via choice
  - [x] Session counters: dialogue windows, choices/transitions, NPC lines,
        objective snapshots; elapsed capture time
  - [x] Expand/collapse back to the full window; remember overlay position
- [x] **Live capture feed (main window)**
  - [x] Pretty-printed feed of captured events: speaker, excerpt, choices with
        the selected one marked
  - [x] Color-coded by type: dialogue / mission update / NPC / progress /
        reacquisition / error
  - [x] Raw progress-string log preserved behind a "Show raw log" toggle
- [ ] **Auto-name output from mission title**
  - [ ] Default output stays timestamped at start
  - [ ] On stop, read the mission title the renderer inferred and offer to
        rename JSONL + transcript to a slug (e.g. `crossroads-at-crateris.jsonl`)
  - [ ] Collision-safe suffix (`-2`, `-3`) when the file already exists
  - [ ] Checkbox to always rename without asking
- [ ] **Global hotkeys**
  - [ ] `RegisterHotKey` Ctrl+Alt+S start/stop toggle that works while STO has
        focus
  - [ ] Hotkey shown in tooltips/UI; graceful fallback if registration fails
  - [ ] Optional second hotkey to show/hide the overlay

## Backlog (later)

- [ ] **Session summary screen on stop** — results card with counts, duration,
      unplayed choices (branches for a second run), fallback-scan warnings,
      open buttons
- [ ] **Recent captures list** — past sessions with open transcript /
      re-render / show in folder per row
- [ ] **Anchor history dropdown** — combo box remembering previously used
      anchors
- [ ] **Dark mode** — full dark theme for the main window
- [ ] **Wikitext export** — STO Wiki mission-page formatter (depends on the
      transcript-to-wikitext converter, not just UI)
- [ ] **Player/ship placeholder substitution** — setting to replace captured
      player and ship names (e.g. `R.R.W. ...`) with `<player ship>` style
      placeholders in rendered transcripts
