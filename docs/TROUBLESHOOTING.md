# Troubleshooting

Logs are in `%LOCALAPPDATA%\ALMARFELD\SubFlow\logs` (**Settings → Open data folder**). A failed file's message in the Queue or History usually says what went wrong.

## "FFmpeg / FFprobe not found"
- Install FFmpeg with `winget install Gyan.FFmpeg` and restart SubFlow; WinGet's location is detected automatically.
- If you install it by hand, either put the `bin` folder on `PATH`, copy `ffmpeg.exe` and `ffprobe.exe` into a `tools\` folder next to `SubFlow.exe`, or set both paths in **Settings → Tools**.
- After installing, press **Refresh** in **Settings → Tools** (or restart SubFlow).

## "Ollama is not running" / no models listed
- Start Ollama from the Start menu (it runs in the tray) or run `ollama serve`.
- Check that `http://127.0.0.1:11434` opens in a browser, or set the right endpoint in **Settings → Ollama**.
- If no models are listed, pull one with `ollama pull qwen3:8b`, then press the refresh button on the Translation page. SubFlow never downloads models by itself.

## Translation is slow
- On a GPU with enough VRAM, a 25-minute episode with about 400 lines takes 1–3 minutes per language with an 8B model. On the CPU it can be 10× slower.
- If the model does not fit in VRAM, use a smaller one (`gemma3:4b`, `qwen3:4b`).
- Keep **Settings → Processing → parallel files** at 1 for a single GPU; parallel jobs only compete for it.
- Re-runs and retries use the translation cache and are almost instant.

## "Incomplete translation" / a language failed
SubFlow refuses to write a track with missing or invented lines. If the model keeps returning the wrong number of lines, the wrong alphabet, or untranslated text, that language fails for that file and the other languages continue. The file then shows *Completed with warnings*.
- Use a stronger or more multilingual model, such as `qwen3:8b` or larger, or `gemma3:12b`. Tiny models often fail on less common languages.
- Lower **Batch size** in Settings (for example to 10).
- Press **Retry**: batches that already succeeded come from the cache.

## A file is "Skipped"
- **No usable subtitles:** the file has no text track, no subtitle file with the same name next to it, and no PGS track. VobSub/DVB image subtitles cannot be read yet.
- **Every target language already exists** and the policy is *Skip*. Choose *Keep both* or *Replace* on the Translation page.
- **The output already exists** and the policy is *Skip*. Choose *Rename* or *Overwrite*.

## "Output validation failed"
After muxing, SubFlow compares the output with the source: stream counts, codecs, resolution, audio channels, subtitle tracks and duration. If anything differs, the output is deleted and the file fails, and the source is left untouched. This is rare. It can happen with damaged sources or unusual containers (AVI with VBR audio, broken timestamps). Try installing MKVToolNix for MKV sources, or remux the source once with another tool first.

## OCR (image subtitles)
- **"Windows has no OCR for <language>":** add that language in Windows Settings → Time & language → Language & region. The OCR component is installed with it; if it is not, open the language's options and add *Optical character recognition*. Restart SubFlow afterwards.
- **"no language tag":** the PGS track is not tagged, so OCR does not know which alphabet to read. Choose the source language on the Translation page.
- **Recognition mistakes** are most common on very small or stylised fonts. Check the "(OCR)" track in the Subtitle editor; the translation is based on that text.

## Wrong source language detected
Set the **Source language** on the Translation page explicitly instead of *Auto-detect*. Auto-detection trusts the track's language tag first, and some files are tagged incorrectly.

## Arabic / Hebrew subtitles display incorrectly
The tracks are UTF-8 and tagged with the correct language. Display problems come from the player: use a recent VLC, mpv or MPC-HC, and a font with full Unicode coverage.

## Cloud engine errors
- **401 / 403:** the API key is wrong or does not have access. Enter it again in Settings; keys cannot be displayed, only replaced.
- **429 / quota:** the provider's rate limit or credit is exhausted. SubFlow retries with back-off, then fails that language.
- **OpenAI-compatible endpoints** must use HTTPS unless they are on `localhost`.

## Reset SubFlow
Close SubFlow, then delete or rename `%APPDATA%\ALMARFELD\SubFlow` (settings and glossaries) and/or `%LOCALAPPDATA%\ALMARFELD\SubFlow` (queue, history, cache, logs). Your videos and outputs are not affected. If `settings.json` is corrupt, SubFlow keeps a `.corrupt` copy and starts with defaults.
