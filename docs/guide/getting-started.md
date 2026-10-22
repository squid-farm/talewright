# Getting started

## Play in your browser

Open the **[Talewright web player](https://squid-farm.github.io/talewright/)**. It works in any modern
browser with nothing to install.

![The story library in the web player](../images/library.png)

- Pick **The Lost Relic** to play the sample story.
- Use **Open a story file** to play your own story from a `.zip` file. [Your first story](first-story.md)
  shows how to make one.

The web player is all you need to write and play stories. Your files stay on your computer: the player
reads the zip inside your browser and doesn't upload it anywhere.

## What you need to write stories

A text editor. Anything that saves plain text works: VS Code, Notepad++, TextEdit (in plain-text mode),
gedit, and so on. A story is just a folder of text files:

```text
my-story/
├── story.yaml      # title, start scene, characters, items, factions
└── story.tale      # your scenes (use as many .tale files and subfolders as you like)
```

> **Tip:** In VS Code, the YAML extension helps with `story.yaml`. Dedicated `.tale` highlighting and
> completion are on the way ([#31](https://github.com/squid-farm/talewright/issues/31)).

## Install the desktop tools (optional)

The desktop tools add a desktop player, the `talewright` command (check stories, publish games) and
the **Author Debugger** (jump to any scene with any game state). They currently run from the source code;
ready-made downloads are planned ([#33](https://github.com/squid-farm/talewright/issues/33)).

1. Install the free [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Install [Git](https://git-scm.com/downloads), then open a terminal and download Talewright:

   ```bash
   git clone https://github.com/squid-farm/talewright.git
   cd talewright
   ```

3. **Linux only:** install WebKitGTK, e.g. `sudo apt install libwebkit2gtk-4.1-0`. Windows and macOS
   already include what's needed.

Run these from the `talewright` folder:

| To… | Run |
|---|---|
| Open the desktop player | `dotnet run --project src/Talewright.Host.Desktop` |
| Open a story in the desktop player | `dotnet run --project src/Talewright.Host.Desktop -- path/to/my-story` |
| Check a story for mistakes | `dotnet run --project src/Talewright.Cli -- validate path/to/my-story` |
| Open the Author Debugger | `dotnet run --project src/Talewright.Cli -- debug path/to/my-story` |
| Publish a game | `dotnet run --project src/Talewright.Cli -- publish path/to/my-story ...` |

The first run takes a minute while .NET builds everything; later runs are quick.

> **Shorter commands:** the rest of the docs write `talewright validate ...`. With a source checkout,
> read that as `dotnet run --project src/Talewright.Cli -- validate ...`.

The desktop player can open story **folders** directly, so you don't need to zip anything while writing.
It also lists the built-in samples.

## Next

[Write your first story →](first-story.md)
