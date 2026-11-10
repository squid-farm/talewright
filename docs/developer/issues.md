# Issue tracking

[GitHub Issues](https://github.com/squid-farm/talewright/issues) is the backlog and the only place
that records issue status. Don't keep a parallel status list in the repository.

- [Development project](https://github.com/orgs/squid-farm/projects/1): status, priority and current work.
- [Milestones](https://github.com/squid-farm/talewright/milestones): which issues belong to which release.
- [Roadmap](../roadmap.md): where the project is headed, in plain language.

## Working on an issue

These steps apply to people and coding agents alike.

1. **Read first.** Read the issue, its parent and sub-issues, and anything blocking it. Keep the
   existing scope and acceptance criteria. Ideas marked `status:idea` or `status:deferred` are not
   approved work.
2. **Check nobody else has it.** Look at assignees, linked PRs, recent comments and the project
   status. Several agents can share one GitHub account, so an assignee alone doesn't prove who is
   working on it. If someone else has claimed it, stop and coordinate.
3. **Claim it.** Assign yourself and leave a comment naming your session or agent, branch, worktree
   and the part you're implementing. Then move the issue to *In progress* and read back the assignee
   and status; a comment doesn't change either.
4. **Work in isolation.** Use its own branch, ideally in a separate `git worktree`.
5. **Open a PR.** Use `Closes #N` only when the PR fully resolves the issue. Use `Refs #N` for
   partial work or progress on an epic. Move the issue to *In review* with test evidence. Leave
   partly done issues *In progress*. For work that is explicitly local-only, record the commit and
   test evidence and say that the PR step is still missing.
6. **Keep GitHub current.** Put scope changes and new tasks in GitHub. Keep the architecture and
   specification docs in this folder in line with what the code actually does.

## Issue intake

The **Issue Intake** workflow applies `status:triage` to new issues from external users,
including submissions through the API. Owners, organization members, and repository
collaborators do not need to label their own issues. Having contributed previously does
not bypass triage. The workflow preserves existing labels and does not relabel an issue
when it is reopened. After reviewing an external submission, a maintainer removes
`status:triage` if it is accepted or closes it if it is not.

Issue templates are not the routing mechanism: API-created issues bypass them.
The workflow uses the repository's built-in token; no additional secret is required.
The repository label `status:triage` must exist before the workflow runs.

Project membership is handled separately by GitHub's built-in **Auto-add to project**
workflow. In the Development project's **Workflows** menu, select this repository,
set the filter to `is:issue is:open`, and save and enable the workflow. Set the
**Item added to project** workflow's default status to **Backlog**. Auto-add must be
enabled through GitHub's UI; its configuration is not exposed by the public API.
Enabling it does not backfill existing issues.

With auto-add enabled, all new open issues belong to the project, including external
reports awaiting triage. The **Triage** view uses `is:open label:status:triage`;
**Backlog** and **Current work** exclude `status:triage`. Current work is also
restricted to Ready, In progress, and In review. Triage is a label and view, not
an additional project status, so removing the label makes an accepted report
visible in Backlog without another status change.
If auto-add is not enabled, add issues explicitly or make them sub-issues of an
epic already in the project.

```bash
gh issue list --repo squid-farm/talewright --state open --limit 100
gh issue view 33 --repo squid-farm/talewright --comments
gh project item-add 1 --owner squid-farm --url https://github.com/squid-farm/talewright/issues/33
gh project field-list 1 --owner squid-farm
```

## Writing an issue

- **Bugs:** severity label (`severity:S1` to `severity:S3`), steps to reproduce, expected and actual
  behaviour, and the regression test you expect.
- **Features:** scope, what is out of scope, acceptance criteria, dependencies, and a milestone or
  parent epic.
- Use GitHub's native blocking links and sub-issues. If an issue touches several epics, link to it
  from the others instead of copying it.
- Keep the project's *Priority* field consistent with the `priority:` label (or the bug's severity).

## Epics

Epics are parent issues with the `epic` label. They group related work across milestones; an issue
has one native parent, and epics link to related issues elsewhere. Don't close an epic because one
child is done. See the [open epics](https://github.com/squid-farm/talewright/issues?q=is%3Aissue+is%3Aopen+label%3Aepic).
