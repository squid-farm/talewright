# Getting started

## Play in your browser

Open the **[Talewright web player](https://squid-farm.github.io/talewright/)**. It works in any modern
browser with nothing to install.

![The story library in the web player](../images/library.png)

- Pick **The Lost Relic** to play the sample story.
- Use **Open a story file** to play your own story from a `.zip` file. [Your first story](first-story.md)
  shows how to make one.

Your files stay on your computer: the player reads the zip inside your browser and doesn't upload it
anywhere. The web player is enough to write and play stories, but once you're writing regularly, the
[desktop tools](#install-the-desktop-tools) make it much quicker to check and test them.

## What you need to write stories

A text editor. Anything that saves plain text works: VS Code, Notepad++, TextEdit (in plain-text mode),
gedit, and so on. A story is just a folder of text files:

```text
my-story/
├── story.yaml      # title, start scene, characters, items, factions
└── story.tale      # your scenes (use as many .tale files and subfolders as you like)
```

> **Tip:** In VS Code, install Red Hat's
> [YAML extension](https://marketplace.visualstudio.com/items?itemName=redhat.vscode-yaml) and the
> [Tale extension](../../tooling/vscode-talewright/README.md). Together they provide `.tale`
> highlighting and structural key completion/validation for `story.yaml`. Run
> `talewright validate` for story-specific checks such as whether your starting scene exists.

## Install the desktop tools

Recommended for anyone writing more than a short story. The desktop tools add:

- the `talewright` command, which checks your story for mistakes and publishes finished games;
- the **Author Debugger**, which jumps to any scene with any game state;
- a desktop player that opens story folders directly, without zipping.

For now they run from Talewright's source code. Versioned downloads
([#86](https://github.com/squid-farm/talewright/issues/86)) and ready-made apps
([#33](https://github.com/squid-farm/talewright/issues/33)) are planned.

1. Install the free [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Download [Talewright as a zip](https://github.com/squid-farm/talewright/archive/refs/heads/main.zip)
   and extract it somewhere handy. Open a terminal in the extracted `talewright-main` folder.
3. **Linux only:** install WebKitGTK, e.g. `sudo apt install libwebkit2gtk-4.1-0`. Windows and macOS
   already include what's needed.

Run these from the Talewright folder:

| To… | Run |
|---|---|
| Open the desktop player | `dotnet run --project src/Talewright.Host.Desktop` |
| Open a story in the desktop player | `dotnet run --project src/Talewright.Host.Desktop -- path/to/my-story` |
| Check a story for mistakes | `dotnet run --project src/Talewright.Cli -- validate path/to/my-story` |
| Open the Author Debugger | `dotnet run --project src/Talewright.Cli -- debug path/to/my-story` |
| Publish a game | `dotnet run --project src/Talewright.Cli -- publish path/to/my-story ...` |

The first run takes a minute while .NET builds everything; later runs are quick. To update, download
the zip again.

> **Using Git?** `git clone https://github.com/squid-farm/talewright.git` instead, and `git pull` to
> update.

> **Shorter commands:** the rest of the docs write `talewright validate ...`. Until ready-made
> downloads arrive, read that as `dotnet run --project src/Talewright.Cli -- validate ...`.

The desktop player can open story **folders** directly, so you don't need to zip anything while writing.
It also lists the built-in samples.

## Next

[Write your first story →](first-story.md)
