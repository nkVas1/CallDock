# Сторонние компоненты

CallDock распространяется под лицензией MIT (см. [LICENSE](LICENSE)). В сборку входят компоненты других авторов;
их лицензии перечислены ниже. Все они допускают свободное распространение в составе программы.

| Компонент | Версия | Лицензия | Что делает в CallDock |
|---|---|---|---|
| [.NET](https://github.com/dotnet/runtime), [WPF](https://github.com/dotnet/wpf), [Windows Forms](https://github.com/dotnet/winforms), [ASP.NET Core](https://github.com/dotnet/aspnetcore) | 10 | MIT, © .NET Foundation and Contributors | среда выполнения, окна, значок в трее, локальный мост для расширения Chrome |
| [WPF UI](https://github.com/lepoco/wpfui) | 4.3.0 | MIT, © Leszek Pomianowski and WPF UI Contributors | оформление Fluent; включает [Fluent UI System Icons](https://github.com/microsoft/fluentui-system-icons) (MIT, © Microsoft Corporation) |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | 8.4.2 | MIT, © .NET Foundation and Contributors | связка интерфейса и логики |
| [NAudio](https://github.com/naudio/NAudio) | 3.1.0 | MIT, © Mark Heath | захват микрофона, системного звука и звука приложений (WASAPI) |
| [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib) | 7.0.1 | MIT, © Sverre Kristoffer Skodje | запись экрана и окон в H.264 |
| [Whisper.net](https://github.com/sandrohanea/whisper.net) | 1.9.1 | MIT, © sandrohanea | распознавание речи; включает [whisper.cpp и ggml](https://github.com/ggml-org/whisper.cpp) (MIT, © The ggml authors) |
| Модели [Whisper](https://github.com/openai/whisper) в формате GGML | — | MIT, © OpenAI; конвертация — [ggerganov/whisper.cpp](https://huggingface.co/ggerganov/whisper.cpp) (MIT) | скачиваются по желанию пользователя, в сборку не входят |
| [Velopack](https://github.com/velopack/velopack) | 1.2.161 | MIT, © Velopack Ltd. | установщик и автообновление |
| [Microsoft.Data.Sqlite](https://github.com/dotnet/efcore) | 10.0.12 | MIT, © Microsoft Corporation | индекс архива и полнотекстовый поиск |
| [SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) | 2.1.12 | Apache-2.0, © SourceGear, LLC | доступ к SQLite |
| [SQLite](https://sqlite.org) | — | общественное достояние | база индекса архива |
| [FFmpeg](https://ffmpeg.org) | 8.1.3 | LGPL-2.1-or-later | сжатие во FLAC, экспорт, воспроизведение записей вкладок, запись потоков |
| Распространяемый пакет Microsoft Visual C++ | 14.x | условия лицензии Microsoft Visual Studio для распространяемого кода | среда выполнения C++ для распознавания речи и записи экрана |

## FFmpeg

В папке `tools` лежит сборка FFmpeg с разделяемыми библиотеками под лицензией LGPL версии 2.1 или более поздней
(вариант `lgpl-shared`, без компонентов GPL и без несвободных компонентов):

- сборка: [BtbN/FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds), архив
  `ffmpeg-n8.1.3-6-gff48edd8b2-win64-lgpl-shared-8.1.zip`, SHA-256
  `1c9af2356443fec537fe1a64a5b33cb4c54fa212ad6590464423b3437e1aaa44`;
- исходный код: [FFmpeg, коммит ff48edd8b2](https://github.com/FFmpeg/FFmpeg/commit/ff48edd8b2) (ветка выпусков 8.1),
  скрипты сборки и исходники библиотек — в репозитории BtbN/FFmpeg-Builds;
- полный текст лицензии — `tools/FFmpeg-LICENSE.txt` рядом с программой.

CallDock вызывает `ffmpeg.exe` и `ffprobe.exe` отдельными процессами и не изменяет FFmpeg. Библиотеки можно заменить
собственной сборкой той же версии: достаточно положить файлы в папку `tools`.

## Microsoft Visual C++

Файлы среды выполнения C++ (`vcruntime140*.dll`, `msvcp140*.dll` и другие из папки `Redist` Visual Studio) включены
рядом с программой, как это разрешают условия лицензии Visual Studio для распространяемого кода, — чтобы не требовать
отдельной установки и прав администратора.
