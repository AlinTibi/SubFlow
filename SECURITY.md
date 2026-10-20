# Security policy

## Supported versions

Security fixes are made for the latest release only.

## Reporting a vulnerability

Please **do not open a public issue** for a security problem. Instead:

- email **contact@almarfeld.com** with the subject "SubFlow security", or
- use GitHub's *Report a vulnerability* button (Security tab), if it is enabled for this repository.

Include the SubFlow version, what an attacker could do, and steps to reproduce it. You will get an answer within a few days. Fixed issues are credited in the release notes unless you prefer otherwise.

## What SubFlow does to stay safe

- **External programs** (FFmpeg, FFprobe, mkvmerge, winget, Ollama) are started with an argument list, never through a shell. A file name therefore cannot inject a command.
- **Source videos are never written to.** Outputs go to a temporary file, are validated, and are then renamed into place.
- **API keys** for cloud engines are encrypted with Windows DPAPI for the current user. They are never shown again and never logged.
- **No telemetry.** The only network calls are:
  - to the translation engine you chose (local Ollama by default);
  - to GitHub, and only when you enable the update check or press "Check now".
- **Remote OpenAI-compatible endpoints** must use HTTPS. Plain HTTP is accepted only for `localhost`.
- **No administrator rights.** SubFlow runs, and its installer installs, without them.
