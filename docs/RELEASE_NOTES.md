# ALMARFELD SubFlow v1.2.1

Translate subtitle tracks with local Ollama or an explicitly selected cloud provider, review results and export new MKV/subtitle copies. Supports batch queues, sidecar text subtitles, Windows PGS OCR, glossary and translation cache.

- Removes the selected queue rows reliably with row actions, Delete or the context menu, including multiple selection. Removing a source from the Videos list never deletes the file.
- Prevents duplicate queue entries when starting the same files again; repeated and overlapping folder imports retain one video entry per file.
- Removed waiting items are skipped, and running-item removal asks before cancellation while the remaining queue continues. Cancelled rows replaced or cleared from the queue do not reappear after restart.

- Preserves existing SRT files with Skip, including MKV-plus-subtitle export, and protects existing subtitles in the source folder.
- Installer and portable Windows x64 ZIP; self-contained .NET runtime, no WebView2 requirement.
- Starts with an empty library, queue and history. No videos, subtitle examples, models or user profiles are distributed.
- FFmpeg/FFprobe are required separately for video processing. Local translation requires a separately installed Ollama and suitable model; optional MKVToolNix improves MKV workflows. Setup/download actions require user confirmation.
- Local translation uses the configured local server. Optional cloud providers transmit subtitle text after consent and may charge; video/audio are not uploaded. No telemetry.
- OCR and AI translation can be wrong: review output. Metadata checks do not certify translation accuracy or verify every media packet.
- Windows binaries are not Authenticode signed. Verify downloads against the SHA-256 file; signed Git tags and provenance are separate from Windows code signing.
