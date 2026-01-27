# Copilot Instructions (RatScanner for Arc Raiders)
***When the task requires multiple steps or non-trivial changes, present a detailed plan using #planReview and wait for approval before executing.
If the plan is rejected, incorporate the comments and submit an updated plan with #planReview.
When the user asks for a step-by-step guide or walkthrough, present it using #walkthroughReview.
Always use #askUser before completing any task to confirm the result matches what the user asked for.***
## Big Picture
- WPF host with Blazor WebView overlays. Passive HUD overlay + interactable overlay are separate windows.
- Core app flow: `RatScannerMain` bootstraps scanning, `StateDetectionManager` performs periodic screen capture + OCR, `MapOverlayManager` holds minimap state, `PlayerStateManager` stores per-session state (quests, workbench, map).
- Global hotkeys are handled by `HotkeyManager` and `UserActivityHelper` low-level hooks.

## Key Components & Data Flow
- Screen capture + OCR state detection: `StateDetectionManager` (timer-driven, 2s interval by default). It decides UI state and triggers extraction per state.
- Map/minimap: `MapOverlayManager` is the singleton source of map position + map image. Map extraction happens only when map view is detected (and now can be hotkey-gated).
- UI overlays:
  - Passive HUD: `Pages/Overlay/Index.razor` (includes `MinimapOverlay.razor`).
  - Interactable overlay tools: `Pages/InteractableOverlay/Index.razor`.
- Game data: `ArcRaidersData` + `RaidTheoryDataSource` load from RaidTheory cache (downloaded to temp cache). Map images + labeled maps are read from `Data/maps` or cached raid data.

## Conventions & Patterns
- Many OCR methods are throttled with per-feature cooldowns (see `StateDetectionManager` fields like `_lastQuestExtractUtc`). Preserve those throttles when adding work.
- Map extraction uses multiple methods (OCR name + cyan marker). Do not assume a single method is reliable.
- `SettingsVM` is the UI-facing config model. It reads/writes `RatConfig` and triggers `RatConfig.SaveConfig()`.
- Hotkeys are registered centrally in `HotkeyManager`. If you add a new hotkey, wire it there and update `RatConfig` + `SettingsVM`.

## Build & Publish
- Development: open `RatScanner.sln` in Visual Studio and build.
- Publish: run `publish.bat` in repository root; output goes to `publish/`.
- Dev setup requires copying the `Data` folder from a release into `RatScanner/Data/` (see README).

## Integration Points / External Dependencies
- OCR: Tesseract trained data in `Data/traineddata` (auto-downloads `eng.traineddata` if missing).
- Image capture: `System.Drawing` + Win32 screen capture.
- Overlay: WebView2 + MudBlazor UI.

## Practical Tips for Changes
- Keep OCR regions small and targeted to reduce lag; avoid adding new full-screen OCR passes.
- If adding map logic, prefer hotkey-triggered capture to avoid background CPU spikes.
- Overlay capture exclusion is a user setting (`RatConfig.Overlay.ExcludeFromCapture`); avoid assuming overlays are always hidden in screenshots.
