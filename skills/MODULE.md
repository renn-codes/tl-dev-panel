# Module Design
This skill is relavent to this module's design, coding, and testing prompts. It details Compiling/Running The Module, and Process Management. It may include Ground Rule Overrides that take precedence of the **Ground Rules**.

## Compiling/Runing The Module
- Each build of the module should be a "clean" full server build (running the "Clean", Cmake", "Build", "Install" commands defined in this document), unless told otherwise.
- The built server is in `/roc/claude/live/bin`, and is where the "Worldserver" and "Authserver" commands are run from.

## Module Configuration File Template
- On compile, the module's new configuration file should generated as a `conf.dist` file, so it does not overwrite a previously edited configuration file (as a `.conf` file).

## Process Management
You are not allowed to force stop the following server-related processe if they become unresponsive after your expected response windows. Give them an additional repsonse window period, then report to me which is hanging, and possible causes and their fix:
- `authserver`
- `worldserver`
- `mysql`
- `mysqld`

### Common Process Commands
- Start Worldserver: `screen -dmS world ./worldserver`
- Start Authserver: `screen -dmS auth ./authserver`
- Authserver Shutdown (in `authserver` cli): Ctrl + C
- Worldserver Shutdown (in `worldserver` cli): `server shutdown 0h0m10s`

# Ground Rule Overrides
**Apply these overrides ONLY when this datafile is used for a task in the current promt.**
**Revert to the prior version of the approrpiate rule in *CLAUDE.md* once all tasks needing these overrides have completed.**

### Superceding the Ground Rule for MySQL modifications:
After all generated .sql files have been presented for the current prompt, explain what each one does and changes. Then give me the option to have you install them individually. You will prompt me for the needed credentials to use during this session.
If I decline to have you install them, provide the information needed for the correct installation of the generated .sql files, and ask me to install them manually. If I decline, you may resume further instructions as long as it does not involve starting the `authserver` or `worldserver`, until I confirm these changes to the database have been made.