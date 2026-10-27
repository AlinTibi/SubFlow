# ALMARFELD SubFlow: User guide

## 0. Setup (first start)

The **Setup** page lists everything SubFlow needs, in order, with one button per item. Each button asks before it installs or downloads anything:

1. **FFmpeg** reads and writes video files (required). **Install FFmpeg** uses the Windows Package Manager; **Download page** opens the manual download.
2. **Ollama** runs the AI on your PC (required for free local translation). Use **Install Ollama**, or **Start Ollama** if it is installed but not running.
3. **AI model.** Pick one from the list and press **Download**; progress is shown in the app and the model is selected automatically when it finishes.
   - *Qwen3 8B* (about 5.2 GB, 8 GB graphics card): recommended for most PCs.
   - *Gemma 3 4B* (about 3.3 GB): for laptops and older graphics cards.
   - *Gemma 3 12B* or *Qwen3 14B* (8–9 GB): best quality on strong graphics cards.

   **Use this model** switches between models you already have.

Optional items: MKVToolNix (better MKV writing) and Windows OCR languages (for Blu-ray image subtitles).

While something required is missing, the Dashboard shows a notice with an **Open setup** button.

## 1. Dashboard

The dashboard shows whether everything SubFlow needs is available:

- **FFmpeg / FFprobe** are required.
- **MKVToolNix** is optional.
- **Ollama** shows whether the server is running and which models it has.
- **Windows OCR** lists the OCR languages installed in Windows (used for PGS image subtitles).

Next to anything missing there is a button that fixes it:
- **Install FFmpeg / MKVToolNix / Ollama** runs the Windows Package Manager (`winget install …`) in a visible console window. winget may ask you there to accept the package licence.
- **Start Ollama** starts Ollama if it is installed but not running.
- **Download qwen3:8b** runs `ollama pull qwen3:8b` (about 5 GB) when Ollama has no model yet.

Every button asks for confirmation first, and nothing is downloaded without it. The dashboard also shows the queue status and the active engine. The badge at the bottom of the sidebar tells you whether text stays on this PC (local engine) or goes to a cloud service.

## 2. Videos

- Add videos with **Add files** or **Add folder**, or drag files and folders anywhere onto the window. Folders are scanned recursively; you can turn this off in Settings. The supported containers are MKV, MP4/M4V, MOV, AVI, WebM, TS/M2TS and others that FFmpeg reads.
- Every file is probed with FFprobe and shows its subtitle tracks: language, codec, title, default/forced flags.
  - **Text** tracks (SRT/SubRip, ASS/SSA, WebVTT, MP4 `mov_text`) can be translated.
  - **PGS image** tracks (Blu-ray) are read with Windows' built-in OCR, then translated. This needs the OCR language for that track's language to be installed in Windows; the Dashboard lists the OCR languages you have.
  - **VobSub and DVB** image tracks cannot be read yet.
  - Image tracks are always kept in the output untouched.
- **Subtitle files next to the video** are found automatically if they share its name:
  - `Movie.srt`, `Movie.en.srt`, `Movie.eng.forced.ass`, `Movie.English.sdh.vtt` and similar;
  - the language is taken from the file name.

  They are listed under the video's tracks as *file*.
- **Which source is used:**
  1. an embedded text track;
  2. otherwise a subtitle file next to the video;
  3. otherwise a PGS track read with OCR.

  Files in one of your target languages are never used as the source, so SubFlow's own `.ro.srt` outputs are not translated again.
- **Choosing the source track.** If a file has several text tracks, SubFlow picks one in this order:
  1. a track in the source language you chose;
  2. the default track;
  3. a full track rather than a "forced"/"signs" track;
  4. the track with the most lines.

  You can see the choice in the file details.
- Remove files from the list at any time. This never touches the files on disk.

## 3. Translation

- **Source language:** *Auto-detect* (recommended) reads the track's language tag and, if it is missing or wrong, detects the language from the text. You can also force a source language.
- **Target languages:** tick any number of languages; use the search box to filter by name, native name or code. A target equal to the source language is skipped.
- **Engine**

  | Engine | Cost | Where the text goes |
  |---|---|---|
  | Ollama | free | your Ollama server (local by default) |
  | OpenAI-compatible | depends on the endpoint | that endpoint. A `localhost` server such as LM Studio or llama.cpp is local and free. |
  | DeepL | paid API (DeepL Free keys work) | DeepL |
  | Google Cloud Translation | paid API | Google |

  Before the first job of a session that uses a cloud engine, SubFlow asks for your confirmation and says whether the engine may charge you.
- **Model (Ollama):** SubFlow lists the models you have installed and suggests a good multilingual one. Use the refresh button after `ollama pull`.
- **If a video already has a track in a target language:**
  - **Skip** (default) leaves that language out for that file.
  - **Keep both** adds the AI track next to the existing one.
  - **Replace** removes the existing track of that language from the output. SubFlow asks you to confirm.
- **Output**
  - *MKV* (default): a new `.mkv` with all original streams plus the new tracks.
  - *Subtitle files*: sidecar files such as `Episode 01.ro.srt` (ASS stays ASS).
  - *Both*.
- **Output folder:** empty means a `SubFlow Output` folder inside each imported folder. *Preserve folder structure* recreates `Season 1\...` under the output folder.
- **If the output file already exists:** *Skip*, *Overwrite* (only the earlier SubFlow output, never the source), or *Rename* (`Episode 01 (2).mkv`).
- **Make the first new track default** sets the default flag on the first new subtitle track and clears it on the others.

Press **Start**. The jobs are saved to the queue and processing begins. You can keep adding videos while a batch runs.

## 4. Queue

Waiting items are saved between sessions. The Dashboard shows how many are waiting: **Continue** starts that queue, and **Open queue** (or the **Files waiting** card) lets you review or remove items first. Removing an item from the list does not delete its source file. Stopping a running item requires confirmation.

Each file goes through these steps: probe → choose track → extract → detect language → translate (one language at a time) → mux → validate → move into place.

- **Pause / resume** is cooperative: the current step finishes, then work waits.
- **Cancel** stops the batch. Partly written temporary outputs are deleted, and finished files are kept.
- **Retry failed** re-runs failed files and files that finished with warnings. **Retry** on one row re-runs only that file. Lines that are already translated come from the cache, so a retry is fast.
- **If SubFlow was closed during a batch,** unfinished files show as *Interrupted*. Press **Start / resume** to continue.
- **Statuses**
  - *Completed* means every requested language was added.
  - *Completed with warnings* means at least one language failed and the others were added. The message lists which.
  - *Skipped* means there was nothing to do, for example no text track, or every target already exists.
  - *Failed* means the file could not be processed. The message says why, and the full details are in the log.

## 5. History

Every finished file is recorded with its status, languages, engine, model, duration and output path. **Export report** writes a CSV file. **Clear history** removes only the records, never any files.

An empty History explains that completed translations will appear there. Opening SubFlow again for the same profile restores its existing window when Windows allows foreground activation, rather than starting another worker for that queue. Advanced/portable use can select a separate profile with `--data "profile folder"`; each distinct profile has its own instance.

## 6. Subtitle editor

- **Open files:** choose the translated subtitle file (SRT/ASS/VTT) and, optionally, the original to show next to it. Lines are aligned by timing.
- **Open video:** pick a video and choose its original track and a translated track, for example an "(AI)" track SubFlow added.
- Edit the text in place. Timings are shown and kept.
- **Find / replace**, **Apply glossary**, and **Translate line** (a preview using the active engine; **Use preview** copies it into the line).
- **Saving**
  - **Save** writes back to the translated subtitle file you opened. For a track opened from a video, Save asks for a new file name and never touches the video.
  - **Save as** writes a new file.
  - **Export SRT** writes an SRT copy.
  - **Save into video** writes a *new* MKV with this track added; the original video is not changed.

## 7. Glossary

The glossary makes names and terms consistent.

- **Source**: the term as it appears in the original.
- **Target**: the required translation. Leave it empty if the term must stay unchanged.
- **Language**: which target language the entry applies to. Leave it empty for all languages.
- **Protected**: never translate the term; keep it exactly as it is.
- **Case-sensitive**.
- **Notes**: context passed to the AI.

There are two scopes:

- **Global** entries apply to every job.
- **Project** entries are stored in `subflow.glossary.json` inside a series folder. They apply to every video below that folder and override global entries.

You can import and export CSV or JSON. Only the entries that occur in a batch are sent to the AI. After translation SubFlow checks that the required terms were used, and retries the batch once if they were not.

## 8. Settings

- **Ollama:** endpoint, model, temperature, context length, batch size, timeout.
- **OpenAI-compatible:** endpoint (HTTPS is required unless it is localhost), model, API key.
- **DeepL:** API key, Free or Pro endpoint.
- **Google:** API key.
- **API keys** are encrypted with DPAPI and cannot be read back. To change a key, type a new one; to remove it, use **Clear**.
- **Tools:** custom paths for `ffmpeg.exe`, `ffprobe.exe` and `mkvmerge.exe`.
- **Processing:** number of files processed in parallel (1 is recommended for a single GPU), and recursive folder scan.
- **Interface language:** English or Romanian. It takes effect after a restart.
- **Data:** open the data and log folders, or clear the translation cache.
- **About:** version, licence (MIT), links to almarfeld.com and the project page, and the third-party licences.
- **Updates:** *Check for updates when SubFlow starts* is off by default. When it is on, SubFlow asks GitHub for the latest release at startup and shows a notice in the sidebar if a newer version exists. **Check now** does the same once. No personal data is sent.

SubFlow also remembers the window's size and position between sessions.

### Adding languages

SubFlow ships with 42 languages. To add another one, create `%APPDATA%\ALMARFELD\SubFlow\languages.json`:

```json
[
  { "Name": "Basque", "NativeName": "Euskara", "Iso6391": "eu", "Iso6392B": "baq", "Iso6392T": "eus", "IsRightToLeft": false }
]
```

Restart SubFlow and the language appears in the list.

## 9. How the translation works

- **Markup.** Only the text is sent for translation. Positioning tags, whole-cue italics and markup that wraps a whole line (for example `<i>` on the second line, or ASS `{\i1}...{\i0}`) are put back afterwards, as long as the translation keeps the same number of lines. Markup in the middle of a line, such as one bold word, cannot be mapped onto the translated words; it stays in the original track and is dropped from the translation.
- **Batches.** Cues are sent in batches of about 20 lines, together with a few lines of the previous dialogue for context.
- **Response format.** The model must answer in JSON with exactly one translation per line.
- **Checks on each answer:**
  - a missing or empty line;
  - the wrong script, for example Latin text when Japanese was requested;
  - a line that is much too long;
  - a line copied unchanged from the source;
  - a line break that was dropped;
  - a glossary term that was ignored.
- **Retries.** A failing batch is retried and, if it still fails, split into smaller batches. If a line still cannot be translated, that language fails for that file and SubFlow invents nothing; the other languages continue.
- **Cache.** Results are cached by engine, model, languages, glossary and text, so re-running a batch, or translating the same line in another episode, costs nothing.

## 10. Output and safety

- **Untouched streams.** Video and audio are copied bit for bit. SubFlow checks the result, the source is never modified, and the output is only renamed into place after it passes validation.
- **mkvmerge.** For MKV sources, SubFlow uses mkvmerge when it is installed, because it preserves Matroska specifics best. Otherwise it uses FFmpeg.
- **MP4 sources** become MKV outputs. MP4 `mov_text` subtitles are converted to SRT so that every track works in MKV.
- **Subtitle files and OCR.** When the source text came from a file next to the video, or from OCR, that original text is also added to the MKV (titled, for example, "English" or "English (OCR)"), so the output contains the source language and the translations. OCR jobs always finish *with warnings*, as a reminder that recognised text can contain mistakes; open the track in the Subtitle editor to check it.
