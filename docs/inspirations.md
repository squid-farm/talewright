# Inspirations and choosing your tools

Talewright wouldn't exist without **[Twine](https://twinery.org/)**,
**[Ren'Py](https://www.renpy.org/)**, and
**[ChoiceScript](https://www.choiceofgames.com/make-your-own-games/choicescript-intro/)**.
They are direct inspirations for this work.

These projects and their creators, maintainers, contributors, and communities have made
interactive storytelling more accessible and shown what authors can accomplish with tools
built around writing. Their years of work on engines, authoring tools, documentation, and
games helped make projects like Talewright possible. We are grateful for those efforts.

- **Twine** has opened interactive fiction to writers through visual passage-based authoring
  and a community of story formats, tutorials, and examples. It inspires our commitment to
  approachable authoring and easy sharing.
- **Ren'Py** has given visual novel creators a writing-friendly scripting language alongside
  powerful tools for images, sound, and custom behavior. It inspires our aim to let authors
  start with a story and add complexity when they need it.
- **ChoiceScript**, developed by Choice of Games, has made stat-driven interactive novels
  approachable for authors without prior programming experience. It inspires our focus on
  meaningful choices, consequences, and rules that serve the prose.

## What Talewright does differently

Talewright focuses on **text-first, stat-driven RPG stories**. Talemark keeps prose, dialogue,
choices, and conditions in plain-text `.tale` files, while `story.talewright.yaml` defines
the cast and rules. Stats, skills, skill checks, inventories, factions, XP, and levels are
built-in concepts rather than systems every author must assemble from scratch.

Explicit diverts support branching paths; hubs and storylets offer scenes according to
the current game state. The supplied web and desktop players share those rules, saves,
and Back/Forward history. This combination may suit stories where RPG mechanics and
revisitable, state-dependent content are central.

Talewright is an early preview. The story format can still change between releases. Starting with
1.0.0-preview.1, stories and saves will keep working across 1.x releases. See
[where Talewright is headed](roadmap.md).

## When another tool may be a better fit

| Tool | Consider it when... |
|---|---|
| [Twine](https://twinery.org/) | You want to arrange passages visually, prototype branching fiction quickly, or choose from established story formats and customize a browser-based story with web technologies. |
| [Ren'Py](https://www.renpy.org/) | Your project centers on visual novel presentation, images, audio, or animation; you need mobile releases; or you want Python for custom systems. |
| [ChoiceScript](https://www.choiceofgames.com/make-your-own-games/choicescript-intro/) | You want a prose-first, choice-and-stat-driven novel and its established author community, or you are interested in the Choice of Games or Hosted Games publishing routes. Review their current submission and licensing terms. |

Choose the tool that fits your story, workflow, and release needs. An established framework
may be the better choice when you need a mature ecosystem rather than an evolving preview.
