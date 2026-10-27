# Getting started

## Play in your browser

Open the **[Talewright web player](https://squid-farm.github.io/talewright/)**. It works in any modern
browser with nothing to install.

![The story library in the web player](../images/library.png)

- Pick **The Lost Relic** to play the sample story.
- Use **Open a story file** to play your own story from a `.zip` file. [Your first story](first-story.md)
  shows how to make one.

Your files stay on your computer: the player reads the zip inside your browser and doesn't upload it
anywhere. The web player does not edit your files; you write them in a text editor. See below for
the no-install way to check and share your story.

Want a game that opens as its own website or desktop app, or a faster way to check and test while
writing? The [desktop tools](#optional-install-the-desktop-tools) can do more, but they require
installing developer tools. You do not need them to write or share a simple story file.

## What you need to write stories

A text editor. Anything that saves plain text works: VS Code, Notepad++, TextEdit (in plain-text mode),
gedit, and so on. You write scenes in **Talemark**, Talewright's story-authoring language,
and define your cast and rules in a YAML manifest. A story is just a folder of text files:

```text
my-story/
├── story.talewright.yaml      # game settings and story declarations
└── story.tale      # your scenes (use as many .tale files and subfolders as you like)
```

Story settings such as the ID, title, starting scene, and ruleset go under `game:`. Characters and
other declarations stay at the root of the manifest.

> **Optional editor help:** In VS Code, Red Hat's
> [YAML extension](https://marketplace.visualstudio.com/items?itemName=redhat.vscode-yaml) can help
> with the manifest. The [Talewright VS Code extension](../../tooling/vscode-talewright/README.md)
> adds Talemark highlighting and live story checks with a bundled Linux x64 language server.
> Its README explains supported Linux environments, preview Marketplace installation and the
> VSIX fallback. You can skip both and use any plain-text editor.

## What can I share without the desktop tools?

Zip your story folder and send the zip to your friends. They can open it in the
[web player](https://squid-farm.github.io/talewright/) by choosing **Open a story file**. This shares
only the story files, not a standalone website or app; the web player checks the story when it opens
it. After you edit the story, make a fresh zip and open it again. See the
[first-story tutorial's zip instructions](first-story.md#no-desktop-tools-use-the-web-player).

## Optional: install the desktop tools

The desktop tools are useful for longer stories, but are not required. They add:

- the `talewright` command, which checks your story for mistakes and publishes finished games;
- the **Author Debugger**, which jumps to any scene with any game state;
- a desktop player that opens story folders directly, without zipping.

For now they run from Talewright's source code, so setup is intended for authors comfortable with a
terminal and developer tools. Releases provide versioned source archives; ready-made apps
([#33](https://github.com/squid-farm/talewright/issues/33)) are a separate follow-up.

1. Install the free [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Open [Talewright releases](https://github.com/squid-farm/talewright/releases), choose a version
   (including previews), and download **Source code (zip)**.
   Extract it somewhere handy and open a terminal in the extracted Talewright folder.
3. **Linux only:** install WebKitGTK, e.g. `sudo apt install libwebkit2gtk-4.1-0`. Windows and macOS
   already include what's needed.

Run these from the Talewright folder:

| To… | Run |
|---|---|
| Open the desktop player | `dotnet run --project apps/desktop` |
| Open a story in the desktop player | `dotnet run --project apps/desktop -- path/to/my-story` |
| Check a story for mistakes | `dotnet run --project tooling/cli/src/Talewright.Cli -- validate path/to/my-story` |
| Open the Author Debugger | `dotnet run --project tooling/cli/src/Talewright.Cli -- debug path/to/my-story` |
| Publish a game | `dotnet run --project tooling/cli/src/Talewright.Cli -- publish path/to/my-story ...` |

The first run takes a minute while .NET builds everything; later runs are quick. To update, download
the zip again.

> **Using Git?** `git clone https://github.com/squid-farm/talewright.git` instead, and `git pull` to
> update.

> **Shorter commands:** the rest of the docs write `talewright validate ...`. Until ready-made
> downloads arrive, read that as `dotnet run --project tooling/cli/src/Talewright.Cli -- validate ...`.

The desktop player can open story **folders** directly, so you don't need to zip anything while writing.
It also lists the built-in samples.

## Next

[Write your first story →](first-story.md)
