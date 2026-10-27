# ALMARFELD SubFlow v1.2.3

Translate subtitle tracks with local Ollama or an explicitly selected cloud provider, review results and export new MKV/subtitle copies. Supports batch queues, sidecar text subtitles, Windows PGS OCR, glossary and translation cache.

- Caps local Ollama output using subtitle size and context settings. Output-limit responses are rejected; exhausted transient failures fall back to smaller batches. Unrecoverable segments still fail, and output quality requires review.
- Retains the v1.2.2 improvements below.
- Explains waiting queue items on the Dashboard, with Continue and Open queue actions. The waiting-files card opens the queue.
- Relaunches restore the matching profile window when Windows allows foreground activation. Separate data profiles can run independently; equivalent paths share the same instance lock. Invalid profile arguments show a controlled startup message.
- Adds an explanation to empty History.
- Retains reliable selected-item removal, duplicate prevention, persistent queue state and conservative export behavior from v1.2.1.

- Preserves existing SRT files with Skip, including MKV-plus-subtitle export, and protects existing subtitles in the source folder.
- Installer and portable Windows x64 ZIP; self-contained .NET runtime, no WebView2 requirement.
- Starts with an empty library, queue and history. No videos, subtitle examples, models or user profiles are distributed.
- FFmpeg/FFprobe are required separately for video processing. Local translation requires a separately installed Ollama and suitable model; optional MKVToolNix improves MKV workflows. Setup/download actions require user confirmation.
- Local translation uses the configured local server. Optional cloud providers transmit subtitle text after consent and may charge; video/audio are not uploaded. No telemetry.
- OCR and AI translation can be wrong: review output. Metadata checks do not certify translation accuracy or verify every media packet.
- Windows binaries are not Authenticode signed. Verify downloads against the SHA-256 file; signed Git tags and provenance are separate from Windows code signing.
