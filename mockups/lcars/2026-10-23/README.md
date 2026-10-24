# Loot Goblin LCARS concepts

Approved LCARS design concepts. Implementation is selected for the repositories in Z:\myrepos.txt, with the rollout tracked in Z:\!todo\2026-10-22GOATFUCK0002.md. These images are design references, not runtime or test evidence.

- [Main window comparison](01-main-regular-compact.png)
- [Settings and supporting owners](02-settings-mini-and-auxiliary.png)
- [Exact source-checked prompts](PROMPTS.md)

The image links are the planned sheet filenames; root review publishes the final generated artwork. Regular/compact compare spacing at the same readable text size. A separate Mini, wizard, access loader or browser view stays a separate owner. Selected supporting views are shown at useful scale; other source windows and conditional states retain their existing layouts and actions, rather than each receiving an illegible thumbnail. Generated text and sample values are proposals, not test results.

## Source scope

Source commit: `c995667cf3947e56197159ef01ad82169e0a3fc0`. Existing dirty paths were inspected by count (0); none are included or changed by this mockup task.

Pause/Resume and Alexandrite Start/Stop are conditional source states; show only the idle applicable actions.

## Verified controls and owners

- LootGoblin/Windows/MainWindow.cs:395 — Map Queue; action strip at 1321; sections at 1382,1446,1488,1584,1682.
- LootGoblin/Windows/ConfigWindow.cs:213 — ordered settings tabs; Run fields at 293; Interface at 915.
- LootGoblin/Windows/AlexandriteMapWindow.cs:74 — Poetics/Runs/Runnable fields and mutually exclusive Start/Stop at 121.

## Existing UI source inventory

- `LootGoblin/Windows/AlexandriteMapWindow.cs`
- `LootGoblin/Windows/ConfigWindow.cs`
- `LootGoblin/Windows/MainWindow.cs`

## Shared appearance proposal

Auto / Picker / LCARS remain in that order, with LCARS third. Preserve native actions, IDs, saved scope and semantic status colours. Krangle obfuscates identifying text. Proposed shared UI tones default off; titlebar speaker and Settings use the same value, with no volume slider. Browser controls remain in the browser header; existing script-free owners do not gain unsupported timed fade. This appearance contract is approved for the allowlisted rollout; implementation and local verification remain separate from artwork approval.
