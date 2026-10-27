# Changelog

All notable changes to ALMARFELD SubFlow. Versions follow [Semantic Versioning](https://semver.org).

## 1.2.3 (2026-10-27)

- Adds a Setup page for dependency status, confirmed installation actions, model downloads with progress/cancellation and model selection. Interrupted model-download streams are not treated as successful.
- Refines copied-sentence and missing-line heuristics; retries doubtful batches at smaller sizes. These checks cannot prove translation accuracy.

- Bounds local Ollama output by subtitle size and context settings. Responses stopped at the output limit are rejected rather than accepted as complete translations.
- After transient failures exhaust retries, translation attempts smaller batches; unrecoverable segments still fail clearly. Cancellation and conservative exports are retained.
- Includes the clearer waiting-queue Dashboard and profile-aware activation introduced in v1.2.2.

## 1.2.2 (2026-10-27)

- Dashboard explains waiting queue items and offers Continue and Open queue actions. The waiting-files card opens the queue.
- Relaunching an open profile restores its matching window when Windows permits foreground activation; separate data profiles can run independently.
- Normalizes profile paths and handles invalid profile arguments without a raw startup exception.
- Empty History explains where finished translations appear.
- Retains v1.2.1 selection, duplicate prevention and safe-export protections.

## 1.2.1 (2026-10-26)

- Queue removal targets the chosen row or selection, supports Delete and multiple selection, and updates the displayed counts.
- Files already waiting, running or interrupted are not queued twice. Replacing or clearing cancelled rows no longer restores obsolete entries after restart.
- Removed waiting items are skipped, including when removal coincides with worker startup. A running item can be stopped after confirmation while other items continue.
- Repeated and overlapping folder imports retain one entry per file. Removing a video from the list does not delete its source file.
- Retains v1.2.0 export protections and adds regression coverage for selection, duplicate paths, persistence and scheduling races.

## 1.2.0 (2026-10-20)

- Existing subtitle files are preserved with Skip in both subtitle-only and MKV-plus-subtitle export. Existing source-folder subtitles are preserved even when overwrite is selected; a numbered copy is saved instead. Concurrent file creation is never silently overwritten without an explicit overwrite policy.

- **Windows installer.** It installs for the current user without administrator rights, with a Start menu entry and an uninstaller. The portable ZIP is still available.
- **Open source under the MIT License.**
- **Setup help on the Dashboard:** install FFmpeg, MKVToolNix or Ollama with one click (winget), start Ollama, or download the recommended model. Each one asks for confirmation first.
- **About section** in Settings, plus an **opt-in update check** against GitHub Releases. It is off by default and sends no personal data.
- **Correct source track:** a "forced", "signs" or "songs" track marked as default no longer wins over the full dialogue track. Before this, some Blu-ray rips got a translation of the ending song only.
- **Multi-monitor fix:** the window opens fully on the monitor it appears on, including mixed scaling such as a 4K screen at 150% next to a 1080p screen at 100%. It also remembers its size and position.
- Tool versions are shown briefly ("FFmpeg 7.1"), and the startup checks no longer time out.
- **Code quality:** the recommended .NET analyzers are on with warnings treated as errors, and continuous integration and release automation run on GitHub Actions.

## 1.1.0 (2026-10-19)

- **PGS image subtitles** (Blu-ray) are read with the OCR built into Windows and then translated. The recognised text is added as an "(OCR)" track. It runs locally and no model is downloaded.
- **Subtitle files next to the video** (`Movie.en.srt`, `Movie.eng.sdh.ass`, â€¦) are used as the source when the video has no text track. The original file is added to the MKV too.
- **Markup that wraps a whole line** (for example `<i>` on one line of a two-line cue, or ASS `{\i1}â€¦{\i0}`) is now kept in translations.
- The Videos page shows subtitle files and OCR-readable tracks, and the Dashboard lists the installed Windows OCR languages.

## 1.0.0 (2026-10-19)

First release.

- Batch import of files and folders, FFprobe analysis, and detection of text and image subtitle tracks.
- Extraction of SRT, ASS/SSA, WebVTT and MP4 `mov_text` subtitles. ASS styles are preserved.
- Translation with Ollama (local, default) or with OpenAI-compatible, DeepL and Google Cloud Translation engines. Cloud engines require consent.
- 42 languages including RTL; more can be added through `languages.json`.
- Context-aware batches, validation, retries and batch splitting. SubFlow never invents missing lines.
- SQLite translation cache, and a resumable queue with pause, cancel and retry.
- Global and per-project glossary with protected terms.
- Lossless remux to MKV with FFmpeg or mkvmerge, output validation, and atomic writes. Sources are never modified.
- Subtitle editor (side by side, find/replace, glossary, line preview, save into a new MKV).
- History with CSV export. English and Romanian interface.
