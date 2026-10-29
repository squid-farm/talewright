# Getting started

Talewright stories are folders of text files. Write in **Talemark**, check your changes, then
play the story and share it. We recommend VS Code with the Talewright extension for writing help.
You can also use any plain-text editor and the browser player.

## 1. Set up your story folder

Each story lives in its own folder. Make one now, somewhere easy to find, such as
**Documents > talewright > my-story**. Start with two files inside it:

```text
my-story/
  story.talewright.yaml
  story.tale
```

- **`story.talewright.yaml`** is your story's settings file, also called the **manifest**. It holds
  the title, the starting scene, the characters, items and rules. Every story has exactly one.
  It is written in **YAML**, a simple text format of `name: value` lines where indentation shows
  what belongs to what. The name must end in `.talewright.yaml`.
- **`.tale` files** hold the story itself: scenes, prose, dialogue and choices, written in
  **Talemark**. Start with one `story.tale`. As your story grows, you can add more `.tale` files
  and subfolders; Talewright reads all of them.

Both are plain text files, so any text editor can open them. Make sure your editor does not add
`.txt` to the end of the name. [Your first story](first-story.md) gives you the complete starting
text for both files.

## 2. Set up your editor

The current Talewright extension supports **Linux computers with Intel or AMD 64-bit processors
(Linux x64)**. It is tested on Ubuntu 24.04 and Linux Mint 22.3. Windows, Mac, Linux ARM and
browser-hosted VS Code are not supported by this extension release. On those systems, use a
plain-text editor and the browser player, or the separate story tools described below.

1. Install [VS Code](https://code.visualstudio.com/).
2. In **Extensions**, install **Talewright** by **squid-farm** from
   [the Marketplace](https://marketplace.visualstudio.com/items?itemName=squid-farm.talewright).
   Choose **Switch to Pre-Release Version** for preview releases.
3. Install [YAML by Red Hat](https://marketplace.visualstudio.com/items?itemName=redhat.vscode-yaml)
   for help with the story settings file.
4. Open your story folder using **File > Open Folder**.
5. When VS Code asks whether you trust the authors of the files in this folder, choose
   **Yes, I trust the authors** if it is your own story or you trust its source. Live checks only
   run in trusted folders. Open a `.tale` file; the language indicator in the bottom-right corner
   should say **Talemark**.

The extension highlights your writing, suggests story names and reports mistakes as you edit.
Its live-checking tool, called the **language server**, is included: you do not need to install
.NET or build Talewright. Save your changes before checking or playing the files on disk.
The [extension guide](../../tooling/vscode-talewright/README.md) includes a download-and-install
alternative and system requirements.

### Finding the Command Palette

Talewright's actions live in VS Code's **Command Palette**, a search box for every command. Open it
from the menu with **View > Command Palette**, or press **Ctrl+Shift+P** (**Cmd+Shift+P** on Mac).
A box appears at the top of the window. Type `Talewright` to list the Talewright actions, then
choose one.

## 3. Get the story-checking tools (CLI)

The **Talewright CLI** checks your saved story, makes story ZIPs to share and opens the
**Author Debugger** to test individual scenes. It is a separate download from the extension.

- **In VS Code:** open the Command Palette and run **Talewright: Set Up CLI**. It uses an existing
  installation when available. Otherwise, it offers a portable download or, when the required .NET
  tools are installed, asks for approval to install the CLI. Nothing is installed just by opening
  a story.
- **Without VS Code:** download it from
  **[Talewright downloads](https://github.com/squid-farm/talewright/releases)**: open the newest
  release, expand **Assets** and pick the `talewright-cli-` file for your computer.
  [Get the story-checking tools](cli-installation.md) explains each step.

Windows, Mac and Linux downloads exist; only Linux x64 execution has been verified so far.
The native **Author Debugger** also needs your computer's web-viewing components. Making a separate
website or desktop game still requires the .NET 10 SDK.

After setup, the Command Palette offers:

- **Talewright: Validate Story** to check saved files.
- **Talewright: Publish Story ZIP** to choose where to save story files for a player.
- **Talewright: Open Author Debugger** to jump to a scene and test choices and state.

These are not a Play button or VS Code's F5 debugger. To play from the beginning, open a story
ZIP in the browser player. The Author Debugger is a separate testing window.

## Use another editor and the browser player

Any editor that saves plain text works. On Mac, use TextEdit's plain-text mode.
Write your story, save the files, zip the story folder and open the
**[Talewright web player](https://play.trytalewright.com/)**.

![The story library in the web player](../images/library.png)

- Pick **The Lost Relic** to try a sample.
- Choose **Open a story file** to check and play your own ZIP.

The player reads the ZIP on your computer; it does not upload it or edit your source files.
After making changes, create a fresh ZIP and open it again. It does not automatically refresh
from your editor. See the [ZIP instructions](first-story.md#no-desktop-tools-use-the-web-player).

Send that same ZIP to friends to play in the browser. It contains your story, not a separate
website or app. A ready-to-use general-purpose desktop player is not included in the CLI download.

## Next

[Write your first story →](first-story.md)
