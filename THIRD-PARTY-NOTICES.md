# Third-party notices

ALMARFELD SubFlow includes the following third-party components in its distributed executable.

| Component | Version | License | Source |
|---|---|---|---|
| .NET 8 runtime and WPF (self-contained) | 8.0 | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf |
| Microsoft.Data.Sqlite | 8.0.30 | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw (bundle_e_sqlite3, core, provider, lib.e_sqlite3) | 2.1.12 | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| SQLite (e_sqlite3 native library) | 3.x | Public domain | https://sqlite.org/copyright.html |
| System.Security.Cryptography.ProtectedData | 8.0.0 | MIT | https://github.com/dotnet/runtime |

Only the test project uses xUnit (Apache-2.0), Xunit.SkippableFact (MS-PL), Microsoft.NET.Test.Sdk (MIT) and System.Drawing.Common (MIT); none of them are distributed with the application.

The UI uses the **Segoe UI** and **Segoe Fluent Icons** fonts that ship with Windows. They are not redistributed.

## External programs (not included)

SubFlow starts the following programs if they are installed on the user's computer. They are **not bundled or redistributed** with SubFlow, have their own licences, and must be installed separately:

| Program | Used for | License | Website |
|---|---|---|---|
| FFmpeg / FFprobe | probing, subtitle extraction, remuxing | LGPL-2.1+ or GPL-2.0+ depending on the build | https://ffmpeg.org/legal.html |
| MKVToolNix (mkvmerge) | optional muxer for MKV sources | GPL-2.0 | https://mkvtoolnix.download |
| Ollama | local AI translation server | MIT | https://github.com/ollama/ollama |
| Windows OCR (Windows.Media.Ocr) | reading PGS image subtitles | part of Windows | https://learn.microsoft.com/uwp/api/windows.media.ocr |
| AI models pulled through Ollama | translation | each model's own licence (for example Apache-2.0 for Qwen3, the Gemma Terms of Use for Gemma) | https://ollama.com/library |

Cloud translation services (DeepL, Google Cloud Translation, OpenAI-compatible APIs) are used only when the user configures them with their own API key and gives consent. Their terms of service apply.

---

## MIT License (.NET, Microsoft.Data.Sqlite, ProtectedData)

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Apache License 2.0 (SQLitePCLRaw)

```
Copyright 2014-2024 SourceGear, LLC

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
```

## SQLite

The author disclaims copyright to the SQLite source code. In place of a legal notice, here is a blessing: *May you do good and not evil.* (https://sqlite.org/copyright.html)
