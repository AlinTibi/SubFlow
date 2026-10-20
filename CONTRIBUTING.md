# Contributing to ALMARFELD SubFlow

Thanks for your interest! Bug reports, translations of the interface and pull requests are welcome.

## Getting started

Requirements: Windows 10/11 x64, the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and [FFmpeg](https://ffmpeg.org) on `PATH`. To run the live translation tests you also need [Ollama](https://ollama.com) with a model.

```
git clone <this repository>
cd SubFlow
dotnet build ALMARFELD.SubFlow.sln
dotnet test ALMARFELD.SubFlow.sln
dotnet run --project src/ALMARFELD.SubFlow.App
```

Tests that need FFmpeg, Windows OCR or a running Ollama are skipped automatically when those are missing.

## Project layout

| Project | Contents |
|---|---|
| `src/ALMARFELD.SubFlow.Core` | Subtitle formats, languages, glossary, jobs and the processing pipeline. No UI and no external tools. |
| `src/ALMARFELD.SubFlow.Infrastructure` | FFmpeg/FFprobe/mkvmerge, the SQLite store and cache, settings, DPAPI. |
| `src/ALMARFELD.SubFlow.Translation` | Translation engines (Ollama, OpenAI-compatible, DeepL, Google) and the document translator with validation and retries. |
| `src/ALMARFELD.SubFlow.Ocr` | PGS image subtitles read with Windows OCR. |
| `src/ALMARFELD.SubFlow.App` | WPF application (MVVM), which builds `SubFlow.exe`. |
| `tests/ALMARFELD.SubFlow.Tests` | xUnit unit and integration tests. |

## Guidelines

- **Build without warnings.** Warnings are errors, and the recommended .NET analyzers are on.
- **Never re-encode video or audio, and never modify source files.** Every output is validated before it is moved into place.
- **Never invent subtitle text.** If a line cannot be translated reliably, the language fails for that file.
- **No telemetry, no silent network calls, no silent paid requests.**
- **No copyrighted media in the repository.** Tests generate synthetic videos with FFmpeg, and synthetic PGS subtitles from rendered text.
- **External programs** are started with `ProcessStartInfo.ArgumentList`, never through a shell.
- **User-visible text** goes in `src/ALMARFELD.SubFlow.App/Resources/Strings.resx`, with the Romanian version in `Strings.ro.resx`.
- **User-visible changes** get a `CHANGELOG.md` entry.

## Adding an interface language

1. Copy `Strings.resx` to `Strings.<culture>.resx`, for example `Strings.de.resx`, and translate the values.
2. Add the culture to `SatelliteResourceLanguages` in `Directory.Build.props`.
3. Add it to the language list in `SettingsViewModel.UiLanguages`.

## Releasing (maintainers)

1. Update the version in `Directory.Build.props` and add a `CHANGELOG.md` section.
2. Commit, then tag and push, for example: `git tag v1.2.0 && git push origin v1.2.0`.
3. The *Release* workflow builds, tests and publishes the installer, the ZIP and the checksums.

To build a release locally, run `powershell -ExecutionPolicy Bypass -File build\package.ps1`. It needs Inno Setup 6 for the installer.

## License

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE).
