# Team Ligma Development Panel
A clean and robust web-panel suite for monitoring, compiling and managing Azerothcore private server codebase deployments using CMake and Ninja. It also features live console monitors with command input capablities, and colourized output, for the test server components (Authserver, Worldserver, MySQL). It is written in C#.Net 9 using the Uno framework. 

## Task-Specific Guides
Before starting a task, check if any of these reference files are relevant and read them first:
- `skills/DOCUMENTATION.md`: Guidelines for maintaining documentation.
- `skills/BUILD.md`: Guidelines for server compilation.
- `skills/MODULE.md`: Instructions related to designing and coding the server module.

## Cloud Environment
Use a base system image of Debian 12 (bookworm) for this cloud environment, as that is the target deployment environment.

### Workspace
You will need to make a non-Docker instance of the Azerothcore project and the current verion of MySQL for this panel to compile and monitor.
You are to run the panel using nginx, and put it on port 26767.
You may use the folders/tools in the following locations as needed:
- The project folder this file is located.
- Any tools you install on the cloud session to facilitate building and running the web-panel.

## General Information
This section is to relavent to the entire project. It details Requirements, Resources, Workspace, System Safety, Rules, and Post-Response Tasks.

### Requirements
- The theme is based on the Claude Dark theme-set, with burnt-orange accents.
- There should be 3 sections between which functions are based:
  - Overview
    - This contains a dash for the current build:
      - The current build's number and start date/time.
      - The current general status of the build.
      - Live-updating progress bars for the overall build and the currently building object(s).
      - Live updating text for the current step of the build and the currently building object(s).
      - An overview of system usage and performance metrics, with graphs/guages as appropriate.
  - Build Console
    - This contains build controls and the detailed build status
      - Build controls:
        - Starting a build+install (`ninja -j$(( $(nproc) - 1 ))`, then `ninja install`).
        - Preparing the build directory (`cmake ../ -G Ninja -DCMAKE_INSTALL_PREFIX=/roc/claude/live/ -DCMAKE_C_COMPILER=/usr/bin/clang -DCMAKE_CXX_COMPILER=/usr/bin/clang++ -DWITH_WARNINGS=0 -DTOOLS_BUILD=all -DSCRIPTS=static -DMODULES=static`)
        - Force stopping the currently running build, with a confirmation warning dialog.
        - Preforming a build directory clean (`ninja -t clean`).
      - Build Status:
        - The current build's number and start date/time.
        - The current general status of the build.
        - Live-updating progress bars for the overall build and the currently building object(s).
        - Live terminal output, with scrollback, of the currently running build/install.
  - History:
    - Build history items are sorted by date/time.
    - They have the following in each lone item, in order:
      - The build number.
      - The date/time the build started.
      - The date/time the build completed.
      - The current and/or final status.
      - The compilation result.
      - Option to remove them individually.
      - Option to display the full build/install log, in a dialog with scrollback.
  - Server Monitor:
    - Contains interactive, rich colour console panels for monitoring and sending commands for the following components:
      - Authserver (`/roc/claude/live/bin/authserver`).
      - Worldserver (`/roc/claude/live/bin/worldserver`).
      - MySQL (`mysql -uroot -p`).
   - Panels are set up in two columns on the page.
     - Left column:
      - Worldserver terminal:
        - Accepts single-line inputs.
    - Right column:
      - Authserver terminal (top half):
        - Accepts Ctrl + C as the only input.
      - MySQL terminal (bottom half):
        - Accepts single line inputs.
        - Ability to run .sql scripts uplodaded from the user's local machine.

### Post-Response Tasks
1. After each promt's response, create an appropriately numbered file in `{project_folder}/Prompts/{Module or Track project}/` (using an ascending scheme i.e. 0001.md, 0002.md, etc) in the following format:

```
# Prompt
{prompt here}

# Reponse
{response here}

# File Changes
{filenames, file type (`Code`, `Asset`, `Docs`, etc), and workspace paths of any created/removed/changed files}

# Code Diffs
{code diffs, on a per file basis with correct coloring}

# Image Assets
{Any image assets along with their file paths}
```