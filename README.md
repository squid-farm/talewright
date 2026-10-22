# Talewright

<img width="1408" height="768" alt="Talewright banner art" src="https://github.com/user-attachments/assets/7bb26ecc-f688-4b54-98b3-85eb6fa67bcc" />

<sub>Banner art is AI-generated placeholder art. If Talewright earns its keep, a real artist gets the job.</sub>

**Write branching, stat-driven RPG stories in plain text. Play them on the web or the desktop.**

Talewright is an engine for text-first RPGs and visual novels with skill checks, inventories, factions
and many branches. You write scenes in `.tale` files, a small extension of Markdown, and describe your
cast in one `story.yaml`. No programming required.

```tale
=== temple.entrance
{player:first-name#possessive} torch gutters as **cold air** spills from the door.

* [Climb the collapsed wall] skill-check athletics dc 14
  success: -> temple.upper
  failure: { player:hp -= 2 } -> temple.fall
* [Read the glyphs] (requires: player:int >= 12) -> temple.glyphs
```

![The Lost Relic sample story being played in the web player](docs/images/playing.png)

## Features

- **Plain-text stories.** Markdown prose, dialogue lines, choices, and inline or block `{if}` conditions.
- **Proper RPG rules.** Stats, skills, d20 skill checks, items that modify stats, factions with
  reputation tiers, XP and levels. Use the default ruleset, tweak it, or replace it.
- **Big branching stories.** Hubs and storylets offer whichever scenes are currently available, so you
  don't have to wire every path by hand. A 10,000-scene story [compiles in about 140 ms](docs/developer/performance.md).
- **Grammar handled for you.** Names, pronouns (including custom sets), possessives ("Burns's") and a/an.
- **Errors caught before players see them.** Broken links, unknown items and typo'd stats are reported
  with file and line number when the story loads.
- **Saves, Back and Forward.** Named save slots plus full undo and redo, with consistent results.
- **Optional character creation.** Players can pick names, pronouns, presets and starting stats.
- **Share it anywhere.** Publish a story file, a static website (works on itch.io), or a desktop app.

## Try it now

Open the **[web player](https://squid-farm.github.io/talewright/)** and pick *The Lost Relic*. Nothing
to install.

## Write your first story

1. Create a folder with a `story.yaml` (title, start scene, characters) and a `story.tale` (your scenes).
   You can also copy the [sample story](samples/LostRelic) and edit it.
2. Zip the folder.
3. In the [web player](https://squid-farm.github.io/talewright/), choose **Open a story file** and select
   your zip.
4. Play it. If something's wrong, Talewright lists each problem with its file and line.
5. Fix, re-zip, and reload.

The **[first story tutorial](docs/guide/first-story.md)** walks through this with a complete example.
Keep the **[Tale syntax reference](docs/reference/tale-syntax.md)** handy while you write.

## Install the desktop tools

The web player covers writing and playing. To check stories from the command line, test individual
scenes in the Author Debugger, or publish a finished game, you currently need the free
[.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) and a copy of this repository:

```bash
git clone https://github.com/squid-farm/talewright.git
cd talewright
dotnet run --project src/Talewright.Cli -- validate path/to/your-story
```

Ready-made downloads are planned ([#33](https://github.com/squid-farm/talewright/issues/33)).
See [Getting started](docs/guide/getting-started.md) for the desktop player and Linux notes.

## Share your story

```bash
talewright publish my-story --output my-story.zip                   # story file for any Talewright player
talewright publish my-story -a --target web --output game-web.zip   # static website, e.g. itch.io
talewright publish my-story -a --target desktop --output game.zip   # Windows, macOS and Linux app
```

Running from a source checkout? Use `dotnet run --project src/Talewright.Cli --` in place of `talewright`.
Players don't need .NET. See [Sharing your story](docs/guide/sharing-your-story.md).

## Documentation

| I want to… | Read |
|---|---|
| Learn to write stories | [Author guide](docs/guide/README.md) |
| Look up syntax or an error code | [Tale syntax](docs/reference/tale-syntax.md) · [Error codes](docs/reference/diagnostics.md) · [CLI](docs/reference/cli.md) |
| Extend the engine in C# or contribute | [Developer docs](docs/developer/README.md) · [Contributing](CONTRIBUTING.md) |

All docs start at the **[documentation home](docs/README.md)**.

## Project status

Talewright is in **preview**. Writing, playing, saving, validating, debugging and publishing all work.
Next up: editor support (syntax highlighting, completion, live errors in VS Code), then images, world
simulation and combat. Follow along on the [roadmap](docs/developer/plan.md#6-milestones) and the
[project board](https://github.com/orgs/squid-farm/projects/1).

## License

[MPL-2.0](LICENSE). Use Talewright for anything, including commercial games. Your stories are yours.
Changes to Talewright's own source files stay open.
