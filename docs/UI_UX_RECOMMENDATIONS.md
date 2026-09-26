# Loot Goblin UI/UX Recommendations

**Review date:** 2026-08-18  
**Scope:** UI code review only; no runtime behaviour or implementation changes are included in this document.

## Product goal

Select runnable treasure maps, verify party and plugin readiness, start the next map, and see why automation is waiting.

## Reviewed surfaces

- `LootGoblin/Windows/MainWindow.cs`
- `LootGoblin/Windows/ConfigWindow.cs`
- `LootGoblin/Windows/AlexandriteMapWindow.cs`

## What is already working

- The main window already models map sources, run counts, allowance, current run, navigation, party, dependencies, and commands.
- Map configuration distinguishes inventory, saddlebag, retainer, gathering, and auto-start behaviour.
- Warnings and state colours provide broad operational visibility.

## Prioritized recommendations

| Priority | Recommendation | Rationale and completion signal |
| --- | --- | --- |
| P0 | Put the map queue and primary action first. | The first viewport should answer: which map is next, how many runs remain, is the party ready, and can Start run now. |
| P0 | Consolidate readiness into one blocker list. | Combine login, dependencies, map availability, party, navigation, repair, food, and combat-provider failures into ordered blockers with targeted fixes. |
| P0 | Show source truth directly on each map row. | Display inventory, saddlebag, retainer, gatherable, selected runs, and shortage in one row so users do not have to reconcile separate sections. |
| P1 | Reduce the eight-tab configuration surface. | Keep Run, Maps, Travel/Party, and Interface visible; place Dungeon/Loot, Integrations, trigger lists, and diagnostic controls under Advanced. |
| P1 | Add progress and cancellation to refresh operations. | Map-source scans and location downloads should show the active source, elapsed progress, last successful refresh, and a safe cancel/retry action. |
| P1 | Use sentence-case, outcome-based actions. | Rename `OPEN ADS LOOT OPTIONS` and similar commands to concise actions and explain whether they navigate, change configuration, or immediately automate. |
| P2 | Integrate Alexandrite as a map-mode card. | Keep its unique Poetics math and limits, but launch and monitor it from the same queue/readiness vocabulary as other map runs. |

## Suggested information hierarchy

1. Next map and Start/Stop
2. Map queue with sources
3. Readiness blockers
4. Current run progress
5. Secondary configuration and diagnostics

## Validation checklist

- A new user can identify the primary action and current blocker within five seconds.
- Every disabled control has a nearby plain-language reason and, when possible, a direct corrective action.
- Healthy, warning, error, running, and disabled states remain distinguishable without colour.
- The UI remains usable at narrow window widths and common Dalamud UI scales without clipped labels or unreachable controls.
- Destructive, global, or high-impact actions identify their scope and require confirmation or provide a safe undo.
- Empty, loading, stale-data, success, partial-success, and failure states each provide an appropriate next action.
- Settings clearly identify whether they apply globally, per account, per character, per preset, or only for the current session.
- Advanced diagnostics are still reachable but do not compete with the everyday workflow.

## Recommended implementation order

1. Implement P0 items and validate the primary workflow plus blocker recovery.
2. Implement P1 information-architecture and configuration improvements.
3. Apply P2 polish, then test at multiple UI scales with both fresh and mature configurations.
