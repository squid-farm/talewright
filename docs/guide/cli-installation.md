# Get the story-checking tools

Talewright has an optional tool that checks your story for mistakes and makes a ZIP file to share
with friends. It also opens the **Author Debugger**, a window where you can jump to a scene and try
different choices without playing from the beginning.

The command-line tool, or **CLI**, runs commands you enter in a terminal.
The tool works with any text editor; VS Code is optional.

**You can skip this setup.** Write your story in a text editor, zip its folder, and open the ZIP in
the [web player](https://squid-farm.github.io/talewright/). It checks your story when you open it.
After making changes, make a fresh ZIP and open it again.

## 1. Find the download for your computer

Open [Talewright downloads](https://github.com/squid-farm/talewright/releases) and choose a release.
A release marked **Pre-release** is an early version: some things may not work yet.
Open its **Assets** list to see the files you can download.

Look for a file whose name starts with **`talewright-cli-`**. The rest of the name includes the
version and the kind of computer it is for:

| Your computer | Look for this ending |
|---|---|
| Windows with an Intel or AMD processor | `win-x64.zip` |
| Windows with an ARM processor | `win-arm64.zip` |
| Mac with an Apple M-series chip | `osx-arm64.tar.gz` |
| Mac with an Intel processor | `osx-x64.tar.gz` |
| Linux with an Intel or AMD processor | `linux-x64.tar.gz` |
| Linux with an ARM processor | `linux-arm64.tar.gz` |

Not sure which you have? On Windows, open **Settings > System > About** and look at **System type**.
On a Mac, open the Apple menu and choose **About This Mac**. On Linux, look in your system's
**About** settings for the processor information.

**If there is no `talewright-cli-` file, the tools are not available in that release.**
The first preview release did not include these downloads. **Source code (zip)** is not the
ready-to-use tool; you do not need to download it. Use the web player for now.

These are early downloads. They have been tried on Ubuntu 24.04 Linux computers with Intel or AMD
processors. The Windows, Mac and ARM downloads have been built, but have not yet been tried on
those computers. Linux downloads do not work on Alpine Linux.

## 2. Unpack the download

1. Download the file for your computer.
2. Make a folder somewhere easy to find, such as **Documents > Talewright Tools**.
3. Unpack the download into that folder. On Windows, right-click the ZIP and choose
   **Extract All**. On a Mac, double-click the downloaded file. On Linux, open it with your
   archive manager and choose **Extract**.
4. Open the unpacked folder. Find **`Talewright.Cli.exe`** on Windows or **`Talewright.Cli`** on
   Mac/Linux. If you see another folder instead, open that folder first.

**Keep everything in the unpacked folder together.** The other files are part of the tool.
Moving just `Talewright.Cli` to your desktop will stop it working.

Your computer may warn that it does not recognize this download. Stop if you are unsure;
do not turn off your computer's security protection to make it run. The
[technical setup notes](https://github.com/squid-farm/talewright/blob/main/docs/developer/cli-setup.md)
cover download verification and operating-system security warnings.

## 3. Check that the tool opens

Run the tool from a **terminal** rather than opening it by double-clicking. A terminal is a
window where you enter a command and press **Enter**.

Open a terminal in the folder containing `Talewright.Cli`:

- **Windows:** right-click an empty space in the folder and choose **Open in Terminal**.
  Use the **PowerShell** tab.
- **Mac:** in Finder, select the folder, open the **Finder > Services** menu, and choose
  **New Terminal at Folder**.
- **Linux:** right-click an empty space in the folder and choose **Open in Terminal**, if offered.

Copy the line for your computer, paste it into that window, and press **Enter**:

**Windows:**

```powershell
.\Talewright.Cli.exe --help
```

**Mac or Linux:**

```bash
./Talewright.Cli --help
```

A list of available commands should appear. This confirms the tool is ready to use.
If you see an error instead, keep its exact wording and check **If something goes wrong** below.

## 4. Check your story

Find the folder that contains your story's `story.talewright.yaml` file. Copy that folder's full
location from your file manager. Keep the quotation marks in the examples below: they let folder
names contain spaces.

In the same terminal window, use the line for your computer. Replace `<your user>` with your
computer's username, or replace the entire example path with your story folder's location.
Keep the quotation marks around the path.

**Windows:**

```powershell
.\Talewright.Cli.exe validate "C:\Users\<your user>\Documents\My Story"
```

**Mac:**

```bash
./Talewright.Cli validate "/Users/<your user>/Documents/My Story"
```

**Linux:**

```bash
./Talewright.Cli validate "/home/<your user>/Documents/My Story"
```

The tool reports mistakes with the file and line to look at. Start with the first message, fix it
in your text editor, save the file, and run the same instruction again.

You can use `debug` instead of `validate` to open the Author Debugger. It may need extra computer
setup; checking your story does not require opening the debugger.

### If you use VS Code

The Talewright extension's writing help and these tools are separate. Installing the extension
does not install the CLI.

With the Talewright extension installed, open **Settings**, search for
**Talewright CLI Executable Path**, and enter the full location of `Talewright.Cli.exe` or
`Talewright.Cli` in your unpacked folder. Then open your story folder and use the Command Palette:

- **Talewright: Validate Story** checks for mistakes.
- **Talewright: Publish Story ZIP** lets you choose where to save a ZIP for the web player.
- **Talewright: Open Author Debugger** opens the testing window.

## If something goes wrong

- **No download for your computer?** You can continue writing and testing stories with the web player.
- **The tool cannot find your story?** Give it the folder containing `story.talewright.yaml`,
  not the `.tale` file or the ZIP. Keep quotation marks around the folder's location.
- **The tool will not open, or mentions a missing library?** Your computer may need an extra
  supporting component. Do not delete the other files from the download. The
  [technical setup notes](https://github.com/squid-farm/talewright/blob/main/docs/developer/cli-setup.md)
  list what each computer needs.
- **The Author Debugger will not open?** It needs a desktop window and your computer's web-viewing
  components. Windows may need **Microsoft Edge WebView2**; Linux needs additional packages.
  You can still check and share your story without the debugger.

Checking a story and making a story ZIP do **not** require installing .NET developer tools.
Making a separate website or desktop game still needs extra developer setup; a ZIP for the web
player is the simpler way to share.

Still stuck? [Report a problem on GitHub](https://github.com/squid-farm/talewright/issues/new?template=report-a-problem.yml).
Include your operating system, the Talewright version, what you tried, and the exact error message.
A GitHub account is required. Don't include private story files or personal information.

## Update or remove the tools

To update, unpack a newer download into a different folder. Try it before deleting the old folder.
If you use VS Code, change **Talewright CLI Executable Path** to the new location.
There are no automatic updates.

To remove the tools, delete the folder where you unpacked them. This does not delete your stories
if you kept them in their own folders.
