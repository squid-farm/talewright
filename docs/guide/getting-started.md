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
writing? The [optional tools](#optional-install-the-desktop-tools) can help. You do not need them
to write or share a simple story file. Making a separate website or app needs extra setup.

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

The optional story tools can:

- check your story for mistakes without making a ZIP first;
- make a story ZIP to share with friends;
- open the **Author Debugger**, a testing window where you can jump to a scene and try different
  choices without starting the story again.

Follow [Get the story-checking tools](cli-installation.md) for step-by-step download instructions.
It explains which file to choose and what to do if a download is not available for your computer.
You do not need to build Talewright yourself.

You may see instructions elsewhere that start with `talewright`. That is the name of the typed
story tool, not something you need for the browser player. The setup guide shows exactly what
to type for your download.

There is not a separate ready-to-use desktop player download in this setup. You can play in
your browser instead. If you want help with developer-only setup, the
[technical setup notes](../developer/cli-setup.md) keep those details separate from this guide.

## Next

[Write your first story →](first-story.md)
