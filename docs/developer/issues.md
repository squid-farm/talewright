# Issue tracking

**[GitHub Issues](https://github.com/squid-farm/talewright/issues) is the live source of truth.**
The former local board has been migrated, including descriptions, acceptance criteria, historical
resolutions, priorities, and original FR/BUG identifiers. Do not maintain a parallel status board here.
This document is a stable migration index, not a snapshot of current issue state.

- [Development Project](https://github.com/orgs/squid-farm/projects/1): backlog, current work, bugs, milestone planning, and epics.
- [Milestones](https://github.com/squid-farm/talewright/milestones): M0-M9 release targets.
- [Roadmap](plan.md): architecture, sequencing, and release criteria; issue links carry live execution details.

## Working with issues (humans and bots)

1. Read the issue, parent/sub-issues, and native blocking dependencies. Preserve existing scope and
   acceptance criteria; exploratory/deferred ideas are not automatically approved work.
2. Before coding, check assignees, linked PRs, recent claim comments, and Project status. Assign yourself
   and leave a claim comment naming your session/agent identifier, branch, worktree, and implementation
   slice. Multiple agents may share one GitHub account, so the assignee alone is not evidence of ownership.
   Assignment and Project status are not atomic locks; stop and coordinate if another worker has already
   claimed the task.
3. Use an isolated branch/worktree. Move actionable, unblocked work from Backlog to Ready; move claimed
   work to In progress and completed implementations to In review with test evidence and a linked PR.
   Read back the assignee and Project status before editing; a claim comment does not update either.
   For explicitly local-only work, record the commit and test evidence and identify the missing PR/merge
   step rather than implying a PR exists. Leave partially implemented issues In progress.
4. Use `Closes #N` only for a fully resolved issue. For partial work or epic progress, use `Refs #N`.
   Do not close a parent epic merely because one child is complete. Verify closure and Project state.
5. Put requirement/status changes and new tasks in GitHub. Keep repository architecture/specification docs
   aligned with implemented behavior, and update roadmap issue links when scope changes.

Project Priority is initialized from priority labels (or bug severity); keep them consistent during triage.
The Project starts private to the organization. The CLI/API supports the configured views and filters;
custom milestone grouping/sorting can be adjusted in the GitHub UI. Built-in Project workflows handle
supported status transitions, but adding an unrelated new issue to the repository does not guarantee it
is added to the Project. Add it explicitly, or as a sub-issue of an epic already in the Project.

```bash
gh issue list --repo squid-farm/talewright --state open --limit 100
gh issue view 33 --repo squid-farm/talewright --comments
gh project item-add 1 --owner squid-farm --url https://github.com/squid-farm/talewright/issues/33
gh project field-list 1 --owner squid-farm
```

For new bugs include severity, reproduction, expected/actual behavior, and regression coverage.
For features include scope, boundaries, acceptance criteria, dependencies, and an appropriate milestone
or parent epic. Use native blockers/sub-issues where appropriate; cross-cutting epic references should
link to the same issue, not duplicate it. New issues use GitHub numbers; do not allocate more local FR IDs.

## Legacy bugs

Fixed bugs were imported as closed issues, retaining their original resolution references.

| Legacy ID | GitHub issue | Summary |
|---|---|---|
| BUG-001 | [#1](https://github.com/squid-farm/talewright/issues/1) | Invalid stat ranges |
| BUG-002 | [#2](https://github.com/squid-farm/talewright/issues/2) | Level-dependent trigger reevaluation |
| BUG-003 | [#3](https://github.com/squid-farm/talewright/issues/3) | Duplicate trigger IDs |
| BUG-004 | [#4](https://github.com/squid-farm/talewright/issues/4) | Expanded ZIP limits |
| BUG-005 | [#5](https://github.com/squid-farm/talewright/issues/5) | Trigger cascade limit |
| BUG-006 | [#6](https://github.com/squid-farm/talewright/issues/6) | Arithmetic overflow |
| BUG-007 | [#7](https://github.com/squid-farm/talewright/issues/7) | Mutable ruleset collections |
| BUG-008 | [#8](https://github.com/squid-farm/talewright/issues/8) | Comments auto-declare flags/variables |

## Legacy feature requests

| Legacy ID | GitHub issue | Summary |
|---|---|---|
| FR-001 | [#9](https://github.com/squid-farm/talewright/issues/9) | NPC character sheets |
| FR-002 | [#10](https://github.com/squid-farm/talewright/issues/10) | NPC relationships |
| FR-003 | [#11](https://github.com/squid-farm/talewright/issues/11) | Split story configuration |
| FR-004 | [#12](https://github.com/squid-farm/talewright/issues/12) | Story window/tab title |
| FR-005 | [#13](https://github.com/squid-farm/talewright/issues/13) | Theming |
| FR-006 | [#14](https://github.com/squid-farm/talewright/issues/14) | Host JavaScript |
| FR-007 | [#15](https://github.com/squid-farm/talewright/issues/15) | Publish without samples |
| FR-008 | [#16](https://github.com/squid-farm/talewright/issues/16) | Grammar libraries |
| FR-009 | [#17](https://github.com/squid-farm/talewright/issues/17) | Active modifier display |
| FR-010 | [#18](https://github.com/squid-farm/talewright/issues/18) | Configurable sheet sections |
| FR-011 | [#19](https://github.com/squid-farm/talewright/issues/19) | End-screen content |
| FR-012 | [#20](https://github.com/squid-farm/talewright/issues/20) | Alternative skill model |
| FR-013 | [#21](https://github.com/squid-farm/talewright/issues/21) | Runtime refactoring |
| FR-014 | [#22](https://github.com/squid-farm/talewright/issues/22) | YAML reader refactoring |
| FR-015 | [#23](https://github.com/squid-farm/talewright/issues/23) | Transcript/conversation flow |
| FR-016 | [#24](https://github.com/squid-farm/talewright/issues/24) | Speaker colors |
| FR-017 | [#25](https://github.com/squid-farm/talewright/issues/25) | Hide mechanical notes |
| FR-018 | [#26](https://github.com/squid-farm/talewright/issues/26) | Precompiled story bundles |
| FR-019 | [#27](https://github.com/squid-farm/talewright/issues/27) | Back/forward history |
| FR-020 | [#28](https://github.com/squid-farm/talewright/issues/28) | CLI validation |
| FR-021 | [#29](https://github.com/squid-farm/talewright/issues/29) | Shared partial analysis |
| FR-022 | [#30](https://github.com/squid-farm/talewright/issues/30) | CLI lint |
| FR-023 | [#31](https://github.com/squid-farm/talewright/issues/31) | LSP and VS Code |
| FR-024 | [#32](https://github.com/squid-farm/talewright/issues/32) | Safe autofixes |
| FR-025 | [#33](https://github.com/squid-farm/talewright/issues/33) | Production-readiness gates |
| FR-026 | [#34](https://github.com/squid-farm/talewright/issues/34) | Save compatibility safeguards |
| FR-027 | [#35](https://github.com/squid-farm/talewright/issues/35) | Images/media |
| FR-028 | [#36](https://github.com/squid-farm/talewright/issues/36) | World simulation |
| FR-029 | [#37](https://github.com/squid-farm/talewright/issues/37) | Combat |
| FR-030 | [#38](https://github.com/squid-farm/talewright/issues/38) | Runtime plugins |
| FR-031 | [#39](https://github.com/squid-farm/talewright/issues/39) | Save migration |

## Additional roadmap deliverables

These M2 items previously existed only in the roadmap. Their IDs were assigned during migration.

| Migration ID | GitHub issue | Summary |
|---|---|---|
| FR-032 | [#40](https://github.com/squid-farm/talewright/issues/40) | Desktop/web save stores and player flows |
| FR-033 | [#41](https://github.com/squid-farm/talewright/issues/41) | Standalone publishing |
| FR-034 | [#42](https://github.com/squid-farm/talewright/issues/42) | C# packages and MSBuild |
| FR-035 | [#43](https://github.com/squid-farm/talewright/issues/43) | Character creation |
| FR-036 | [#44](https://github.com/squid-farm/talewright/issues/44) | Richer hubs/storylets |

## Epics

Native sub-issues have one parent. Cross-cutting membership is retained as related links in epic bodies.

| Epic | GitHub parent issue |
|---|---|
| Core reliability and release | [#45](https://github.com/squid-farm/talewright/issues/45) |
| Scalable authoring and content organization | [#46](https://github.com/squid-farm/talewright/issues/46) |
| Player presentation and accessibility | [#47](https://github.com/squid-farm/talewright/issues/47) |
| Ruleset and narrative expressiveness | [#48](https://github.com/squid-farm/talewright/issues/48) |
| Distribution and extensibility | [#49](https://github.com/squid-farm/talewright/issues/49) |
| Session lifecycle and compatibility | [#50](https://github.com/squid-farm/talewright/issues/50) |
