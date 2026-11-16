# Talemark authoring: start here

Talemark is Talewright's language for interactive stories: prose, dialogue, choices and rules in
`.tale` files, with characters and starting state in `story.talewright.yaml`.

**If you use AI in your workflow, this guide can help it understand how to write Talemark.**
Copy this URL into your prompt:

```text
https://raw.githubusercontent.com/squid-farm/talewright/main/docs/guide/talemark-agent-brief.md
```

No skill installation, account integration or project changes are needed. Your assistant needs to
be able to fetch URLs; otherwise, supply this file and the relevant topic files yourself.
This guidance does not guarantee correct output.

## Instructions for the assistant

1. Inspect the author's existing story files and IDs if available. Preserve prose, intent and
   unrelated content. Do not invent syntax, names or extension registrations.
2. Read **only the topic needed for the request**, using the table below. Topic pages are
   self-contained and have no further documentation links to follow. Stop fetching once you have
   the required syntax; do not crawl this repository or read all topics for a small edit.
3. Use current implemented syntax, not another language's conventions or planned features.
   Expressions read state; effects change it. Talemark is not Markdown, Ink or Twine.
4. Validate the whole saved story with `talewright validate <story-folder>` if that tool is
   available. Fix errors without changing the author's intended behavior. Report what you checked
   and what you could not check. Playtest important routes; compilation cannot prove every outcome.

## Choose a topic

Resolve these links relative to this page's URL. Each page contains a complete, compiler-checked
manifest and story; adapt the relevant lines rather than replacing an existing story.

| What the author needs | Read |
|---|---|
| A new story, manifest settings, declarations, file layout or valid names | [Story files](talemark/story-files.md) |
| An if statement, conditional prose, testing a flag, item, visit or expression | [Conditions](talemark/conditions.md) |
| Choices, requirements, unavailable labels, diverts or endings | [Choices and flow](talemark/choices.md) |
| Set a flag, change variables, inventory, stats or standing; automatic triggers | [State and effects](talemark/state.md) |
| Dialogue, substitutions, pronouns, manipulators, formatting, escapes or images | [Reader text](talemark/text.md) |
| Hubs, tags, priorities, once-only storylets or generated choices | [Hubs](talemark/hubs.md) |
| Skill checks, success/failure branches, hidden rolls or custom stats | [Skill checks](talemark/checks.md) |

For "only discover the body if the murder has happened", read **Conditions**, then use the
story's existing murder state. Read **State and effects** too only if asked to record the murder
or discovery. For a complex new story, select the topics for its requested features; do not fetch
the full reference.

## Optional: remember this guidance across tasks

Advanced authors can paste the following into their own `AGENTS.md`, `.github/copilot-instructions.md`,
`CLAUDE.md` or their assistant's equivalent instruction file. Use the location your tool supports;
you do not need all of these files.

```text
When writing or editing Talemark (.tale or story.talewright.yaml files), consult
https://raw.githubusercontent.com/squid-farm/talewright/main/docs/guide/talemark-agent-brief.md
Read only the linked topics needed for the current task; do not crawl the full documentation.
Preserve my prose and existing story IDs. Validate the story when the CLI is available.
```

## Reference and version

The topic pages summarize implemented behavior from the [syntax reference](../reference/talemark-syntax.md),
[operator registry](../reference/talemark-operators.md), [manifest guidance](../reference/talemark-syntax.md#1-storytalewrightyaml)
and [validation guide](testing-your-story.md). These are deeper references, **not required reading**
for routine authoring; the registry's planned changes are not available syntax.

The URL tracks `main`. For an older Talewright installation, use a matching release tag in place
of `main` in the URL; relative topic links then stay on that version.

Measured with tiktoken 0.14.0, `cl100k_base`: this entry is 1003 tokens; Conditions is 816 tokens.
Entry plus all seven topics is 6300 tokens. These are documentation input sizes, not a price
guarantee; model rates, generated output and tool calls also affect cost.
