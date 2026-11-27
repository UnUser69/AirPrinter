# AirPrinter

Настольное приложение для Windows. USB-принтер с уже установленным драйвером Windows становится виден iPhone как принтер AirPrint. Отдельное приложение на телефон не нужно.

Компьютер и iPhone должны быть в одной сети. Пока мост включён, компьютер не должен засыпать. Печать идёт в локальную очередь Windows, а не через SMB.

## Стек

| Слой | Технология |
| --- | --- |
| Язык и рантайм | C# / .NET 8 (`net8.0-windows`) |
| Окно | WPF, интерфейс на русском |
| Трей и печать | Windows Forms: `NotifyIcon`, `PrintDocument`, GDI+ |
| Права | `requireAdministrator` в `src/AirPrinter/app.manifest` |
| Принтеры Windows | WMI `Win32_Printer`, запасной путь — `winspool` (`SetPrinter`) |
| PDF | Docnet.Core 2.6.0, внутри PDFium (`pdfium.dll`) |
| JPEG и PNG | GDI+ `Image.FromStream` |
| URF и PWG Raster | свой разбор PackBits |
| Обнаружение | Apple Bonjour (`dnssd.dll`, служба «Bonjour Service»), если пакет установлен |
| Запасной путь | свой mDNS на UDP 5353, если Bonjour нет |
| Печать с телефона | IPP поверх HTTP, TCP 631, иначе 6310, иначе свободный порт |
| Брандмауэр | COM-интерфейс брандмауэра Windows: входящие TCP выбранного порта и UDP 5353, только частный профиль и только этот exe |
| Настройки | JSON в `%LocalAppData%\AirPrinter` |
| Проверка | `tests/AirPrinter.SelfTest`, тот же код без отдельного тестового фреймворка |

Сторонние пакеты только три: Docnet.Core, System.Management, System.ServiceProcess.ServiceController. Бинарники Apple в проект не входят. Ссылка на установщик Bonjour — страница поддержки Apple `https://support.apple.com/en-us/106380`.

## Как это работает

1. Пользователь выбирает локальный принтер. Виртуальные очереди (PDF, XPS, OneNote, Fax) показываются после физических.
2. По желанию очередь расшаривается средствами Windows. Для iPhone этот общий доступ не используется.
3. `AirPrintBridge` поднимает IPP-сервер на всех интерфейсах (`IPAddress.Any`).
4. В брандмауэре открываются TCP этого порта и UDP 5353. При остановке оба правила удаляются. Группа «Общий доступ к файлам и принтерам» не включается.
5. Если служба Bonjour запущена, имя публикует `dnssd.dll`. Иначе то же имя публикует `MdnsPublisher`.
6. iPhone находит `_ipp._tcp`, подтип `_universal._sub._ipp._tcp` и `_printer._tcp`.
7. Телефон шлёт IPP `Print-Job` или `Create-Job` + `Send-Document`. Тело — PDF, JPEG, PNG, URF или PWG Raster.
8. Страницы становятся растром и печатаются в выбранную очередь через GDI. Драйвер принтера остаётся драйвером Windows.

Закрытие окна прячет его в трей. Второй экземпляр не стартует: мьютекс `Local\AirPrinter.Bridge`.

## Что лежит на диске

Каталог `%LocalAppData%\AirPrinter`:

- `identities.json` — имя очереди Windows → GUID. В IPP уходит `urn:uuid:<guid>`, в TXT Bonjour — тот же GUID без префикса.
- `settings.json` — последний принтер, флаг общего доступа и имя ресурса.

Секретов, учётных записей и облака нет.

## Форматы и протокол

IPP без TLS и без аутентификации. Операции: Print-Job, Validate-Job, Create-Job, Send-Document, Cancel-Job, Get-Job-Attributes, Get-Jobs, Get-Printer-Attributes.

В атрибутах принтера есть `ipp-features-supported: airprint-1.8` и `airprint-1.4`, форматы `application/pdf`, `image/jpeg`, `image/png`, `image/urf`, `image/pwg-raster`, набор размеров бумаги и коллекции `media-col-*`. Вложенные поля коллекции кодируются тегом `0x4A`.

Ограничения, которые уже стоят в коде:

- тело запроса до 200 МБ;
- не больше 8 одновременных соединений;
- не больше 64 HTTP-заголовков и 200 000 chunk;
- PDF до 40 страниц, растеризация около 300 DPI;
- JPEG и PNG до 40 миллионов пикселей;
- URF до 200 страниц, сторона страницы до 20 000×30 000;
- имя пользователя в журнале обрезается до 80 символов;
- список заданий в памяти до 50 штук.

## Порядок чтения для аудита

Читать в этом порядке. Каждый следующий файл опирается на предыдущий.

1. `src/AirPrinter/app.manifest` — процесс всегда с правами администратора.
2. `src/AirPrinter/App.xaml.cs` — единственный экземпляр, необработанные исключения, аргумент `--self-test`.
3. `src/AirPrinter/Services/AirPrintBridge.cs` — порядок старта и остановки: общий доступ, порт, брандмауэр, объявление.
4. `src/AirPrinter/Services/FirewallService.cs` — правила брандмауэра через штатный COM API, без запуска `netsh`. Правило ограничено этим процессом и частной сетью.
5. `src/AirPrinter/Services/IppServer.cs` — кто может подключиться, лимиты, разбор HTTP, операции IPP, очередь заданий. Слушает все интерфейсы, авторизации нет.
6. `src/AirPrinter/Services/IppCodec.cs` — разбор и сборка IPP. Здесь проверять, что коллекции и значения не путаются.
7. `src/AirPrinter/Services/PrintRenderer.cs` — PDFium и разбор растров. Входные файлы приходят из сети и попадают в нативный код.
8. `src/AirPrinter/Services/BonjourAdvertiser.cs` — P/Invoke `dnssd.dll`, текст TXT-записей, порт в сетевом порядке байт.
9. `src/AirPrinter/Services/MdnsPublisher.cs` — свой mDNS, если Apple Bonjour не установлен. Сокет UDP 5353 на всех интерфейсах.
10. `src/AirPrinter/Services/PrinterShareService.cs` — WMI и `winspool`. Общий доступ Windows меняет состояние системы.
11. `src/AirPrinter/Services/IdentityStore.cs` и `AppPaths.cs` — единственные файлы, которые приложение пишет само.
12. `src/AirPrinter/MainWindow.xaml` и `MainWindow.xaml.cs` — что видит пользователь и что запускается по кнопкам.
13. `src/AirPrinter/Services/SelfTest.cs` — ожидаемое поведение без принтера и без телефона.
14. `src/AirPrinter/AirPrinter.csproj` — полный список зависимостей. Других пакетов нет.

Отдельно, не как исходники приложения: спецификации IPP и mDNS (RFC 8010, RFC 8011, RFC 6762, RFC 6763) и страница Bonjour Print Services у Apple. Сверять с ними ответы `Get-Printer-Attributes` и TXT-записи.

## На что смотреть в первую очередь

- IPP слушает все адреса и не проверяет клиента. Любой узел в сети может прислать документ, а PDFium его разберёт.
- Процесс с правами администратора держит этот разбор.
- Правила брандмауэра снимаются в `Stop`. Если процесс убить, правила могут остаться.
- Задания, которые уже печатаются, при остановке не отменяются отдельно.
- В `MdnsPublisher.Start` и в конце `SelfTest.Run` есть запись диагностического NDJSON в `.cursor/debug-f01924.log`. На протокол и печать это не влияет. Для чистого аудита эти две записи лучше убрать.
- `pdfium.dll` приходит с пакетом Docnet.Core. Исходников PDFium в репозитории нет, дизассемблировать его этот проект не умеет.

## Сборка и проверка

Из каталога `AirPrinter`:

```
dotnet run --project tests/AirPrinter.SelfTest/AirPrinter.SelfTest.csproj -c Debug
```

Успех — последняя строка `SELF-TEST PASSED`. Проверяются имя общего ресурса, TXT-записи, разбор URF, JPEG, PDF и PWG, коллекции IPP, chunked `Print-Job` и ответ mDNS на UDP 5353. Регистрация через Apple Bonjour пропускается, если пакет не установлен. Печать на бумагу и появление принтера на iPhone самотест не делает.

Окно:

```
dotnet run --project src/AirPrinter/AirPrinter.csproj -c Debug
```

Запуск запросит права администратора. Для живой проверки выбрать USB-принтер, включить мост и открыть печать на iPhone в той же сети.
