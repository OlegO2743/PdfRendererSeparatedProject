# Current Implementation Status

Актуальный статус проекта `PdfViewer.WinForms` на `2026-04-17`.

Этот файл нужен как короткий и практичный summary:

- что уже реализовано
- на каком этапе находится проект
- какие части PDF реально поддерживаются
- что еще остается добить

Важно:

- проект сейчас является **PDF viewer / renderer**, а не PDF editor
- это кастомный рендерер на `System.Drawing` + WinForms
- `SkiaSharp` в текущей реализации **не используется**

## 1. Текущий этап

Проект уже вышел из стадии "учебный viewer" и находится на стадии:

- рабочий кастомный PDF renderer для значительной части реальных PDF
- активная добивка сложных edge-cases
- приоритет сейчас не в базовом открытии файлов, а в точности рендера и совместимости

По состоянию на сейчас особенно заметный прогресс есть в:

- текстовом рендере
- Type0/CID и embedded font handling
- image XObject support
- `JPXDecode/JPEG2000`
- viewer-side page/thumbnails parity
- object overlay / hit-testing

Недавний важный checkpoint закрыт:

- `poradnik2013.pdf` больше не является активным JPX-блокером
- страница `1` видна корректно
- проблемные JPX-страницы `141/182/188` доведены до близкого визуального совпадения
- остаточный live-viewer hue mismatch был дополнительно уменьшен через display-profile transform в WinForms viewer

## 2. Что уже реализовано

### Парсинг PDF

Реализовано:

- поиск и разбор top-level indirect objects
- stream objects
- прямой и косвенный `/Length`
- обход page tree `/Root -> /Pages -> /Kids`
- наследование `/MediaBox`
- наследование `/Resources`
- поддержка `/Contents N 0 R`
- поддержка `/Contents [ ... ]`
- частичный разбор compressed objects из `ObjStm`

Пока не доведено до полного spec-level уровня:

- полноценные `xref streams`
- полноценные modern object streams как основной способ адресации документа
- incremental update / revision chains

### Графика и page content

Реализовано:

- `q`, `Q`, `cm`
- базовый graphics state
- path operators: `m`, `l`, `c`, `v`, `y`, `h`, `re`
- stroke / fill / even-odd
- clipping через `W`, `W*`
- dash / cap / join / miter
- Form XObject
- nested XObject

Частично:

- часть `ExtGState`
- часть color-management логики

Не реализовано полноценно:

- transparency groups
- blend modes
- spec-level transparency pipeline
- page `/Rotate` из самого PDF
- page boxes `/CropBox`, `/TrimBox`, `/BleedBox`, `/ArtBox`

### Цвета и color spaces

Реализовано:

- `/DeviceGray`
- `/DeviceRGB`
- `/DeviceCMYK`
- именованные resource color spaces
- `/Indexed`
- tiling pattern (`/PatternType 1`) для поддерживаемых случаев

Частично:

- `/ICCBased`
- viewer-side display profile transform для page view / canvas / thumbnails

Не реализовано полноценно:

- spec-level end-to-end color-managed rendering уровня Acrobat/Chrome
- `/Separation`
- `/DeviceN`
- shading patterns

### Текст и шрифты

Реализовано:

- основные text operators: `BT`, `ET`, `Tf`, `Tm`, `Td`, `TD`, `T*`
- `Tc`, `Tw`, `Tz`, `TL`, `Ts`
- `Tj`, `TJ`, `'`, `"`
- `/ToUnicode`
- `/Encoding`
- `/Differences`
- `Identity-H`
- CID widths `/W`
- fallback для base14
- embedded TrueType glyph-path rendering

Частично:

- embedded CFF / Type1C
- Type0 / CID fonts
- сложные embedded formula fonts
- точное совпадение glyph mapping и метрик с browser/Acrobat

Слабые зоны по тексту остаются именно здесь:

- старые научные PDF
- формулы
- специальные математические glyph combinations
- случаи, где нужен очень точный CID/CFF mapping

### Изображения

Реализовано:

- Image XObject
- inline images
- `FlateDecode`
- `DCTDecode`
- `SMask` для части случаев
- image masks для основных 1-bit случаев
- `CCITTFaxDecode` Group 4

Сильно улучшено и уже реально используется:

- `JPXDecode/JPEG2000`

Текущее реальное состояние `JPXDecode`:

- есть внутренний decoder
- есть fallback через WIC, если на машине доступен JPEG2000 codec
- исправлены ключевые проблемы:
  - reversed low/high normalization в inverse `9/7`
  - ошибка в `HH` significance context
  - missing `0.5` normalization для Tier-1 reconstructed coefficients
- проблемные реальные кейсы из `poradnik2013.pdf` существенно исправлены

Ограничения `JPX` все еще есть:

- это не "полная поддержка любого JPEG2000 из любого PDF"
- лучше всего покрыты не-subsampled 1- и 3-component codestream cases
- остаются возможные пограничные случаи на более редких JPEG2000 профилях

Пока не реализовано:

- `JBIG2Decode`
- полноценный `LZWDecode` как image filter
- полное покрытие всех экзотических image filter chains

### Viewer UI

Реализовано:

- открытие PDF
- навигация по страницам
- zoom in / zoom out
- fit width / fit height
- thumbnail strip
- viewer-side rotation
- scroll / pan
- прогресс загрузки
- object overlay mode
- hover / hit-testing по объектам

Дополнительно:

- display-profile transform теперь применяется в live viewer перед показом bitmap
- это улучшает визуальную parity с browser/PDF viewer на текущем мониторе
- для `Ulotka-45202-2022-10-31-1923_D-2022-11-15.pdf` был добавлен более точный fallback по текстовым метрикам и family mapping, что заметно улучшило типографику и spacing
- из viewer UI и кода удалена генерация demo/test PDF; проект сейчас сфокусирован только на открытии и рендере реальных документов
- загрузочный progress bar вынесен из перегруженного toolbar в отдельную верхнюю loading-strip панель, чтобы прогресс открытия PDF был всегда заметен

Недавние изменения по ленте миниатюр:

- подписи под миниатюрами получили отдельную caption-zone и увеличенную высоту, чтобы номера страниц не подрезались
- размер миниатюры теперь вычисляется от текущей ширины боковой панели, а не от фиксированного шаблонного размера
- при resize/scroll/paint были добавлены дополнительные попытки форсировать рендер видимых slots и пересчёт scrollbar-ов

Текущее состояние thumbnail strip на `2026-04-17`:

- базовая адаптация размера миниатюр к ширине панели уже внедрена и визуально работает
- но thumbnail strip пока нельзя считать полностью стабилизированным
- остаются открытые UI-регрессии:
  - при прокрутке могут пропадать номера страниц под миниатюрами
  - у части страниц могут появляться пустые белые placeholders вместо bitmap, пока не случится forced repaint
  - после изменения ширины панели диапазон вертикального scrollbar иногда остаётся устаревшим и не даёт доскроллить до последних страниц
- это сейчас не core PDF-render blocker, а отдельная viewer-side задача на стабилизацию UI-поведения

Текущее состояние smooth scroll на длинных документах:

- базовый режим `Плавно` работает и пригоден для обычных документов
- но на очень длинных документах текущая реализация всё ещё упирается в ограничение scroll-range стандартного WinForms `AutoScroll`
- root cause уже локализован лучше, чем раньше: проблема не столько в памяти, сколько в giant virtual surface / scroll coordinates
- одна попытка исправить это через logical scaling scroll-координат была откатана в том же рабочем цикле, потому что дала дублирование контента, более медленную прокрутку и повышенную нагрузку на CPU
- ещё одна попытка ускорить/виртуализовать smooth mode через скрытие и выгрузку non-resident page controls тоже была откатана, потому что в реальном просмотре дала артефакты, медленную прокрутку и высокий CPU
- следующий правильный шаг здесь не ещё одна латка поверх `AutoScroll`, а перевод smooth-mode к схеме уровня thumbnail strip: page slots + visible-page cache + ручная виртуализованная отрисовка

## 3. Что реально поддерживается по PDF

### Поддерживается хорошо

- классические PDF 1.0-1.4 без экзотики
- обычные текстово-графические документы
- технические и офисные PDF без сложной transparency/document-level логики
- документы с обычными embedded fonts
- документы с `FlateDecode`, `DCTDecode`, базовыми XObject и path operators

### Поддерживается частично

- PDF 1.5+ без тяжелой зависимости от `xref streams`
- modern PDF с частью compressed objects
- `ICCBased`
- Type0/CID/CFF
- `JPXDecode`
- `CCITTFaxDecode`
- сложные формульные шрифты
- мелкие bitmap details / parity с браузером

### Пока не поддерживается полноценно

- `xref streams`
- полноценные modern object streams / full revision chains
- page `/Rotate` из PDF
- page boxes `/CropBox` / `/TrimBox` / `/BleedBox` / `/ArtBox`
- transparency / blend modes / transparency groups
- annotations
- links
- bookmarks / outlines
- AcroForm
- encryption
- digital signatures
- tagged PDF / structure tree
- optional content / layers
- attachments / multimedia / 3D

## 4. На каком этапе совместимость

Если говорить честно, проект сейчас находится между двумя уровнями:

### Уже не "prototype"

- viewer реально работает
- core rendering pipeline не игрушечный
- есть собственный parser
- есть собственный text/image/path renderer
- есть успешные реальные фиксы на проблемных PDF

### Но еще не "Acrobat-class renderer"

- нет полной spec-level поддержки структуры PDF
- нет полной transparency/color-management модели
- нет полной совместимости со всеми font/image corner cases
- остаются feature-gaps на document-level PDF functionality

## 5. Что осталось сделать

### Приоритет 1

- добить сложные embedded formula fonts
- добить точный glyph mapping и текстовые метрики
- добавить page `/Rotate` и page boxes
- довести modern object streams / `xref streams`

### Приоритет 2

- расширить image filter coverage
- довести `JPXDecode` до еще более широкого покрытия
- улучшить `ICCBased` / color-management pipeline
- добавить transparency / blend support

### Приоритет 3

- annotations
- links
- bookmarks
- forms
- encryption / signatures / tagged PDF / layers

## 6. Практический вывод

На сегодня проект уже можно считать:

- рабочим кастомным PDF viewer/rendering engine
- пригодным для значительной части реальных текстово-графических PDF
- особенно сильным в тех областях, где уже были целевые багфиксы по реальным документам

Главные незакрытые зоны сейчас:

- сложные formula fonts
- точная browser/Acrobat parity по тексту
- advanced color / transparency
- full modern PDF structure support
- document-level PDF features
- стабильность thumbnail strip при scroll/resize
- smooth scroll для очень длинных документов
  сейчас в переходе с WinForms `AutoScroll` на внутренние scrollbars и
  собственную scroll-позицию; сборка проходит, но нужен живой runtime-check

## 7. Куда смотреть дальше

Связанные рабочие логи:

- [PDF_SUPPORT_MATRIX.md](/D:/Projects/C%23/PdfRendererSeparatedProject/PdfRendererSeparatedProject/PDF_SUPPORT_MATRIX.md)
- [JPX_WORKLOG.md](/D:/Projects/C%23/PdfRendererSeparatedProject/PdfRendererSeparatedProject/JPX_WORKLOG.md)
- [PORADNIK2013_PAGE_ISSUES.md](/D:/Projects/C%23/PdfRendererSeparatedProject/PdfRendererSeparatedProject/PORADNIK2013_PAGE_ISSUES.md)
- [OBJECT_OVERLAY_WORKLOG.md](/D:/Projects/C%23/PdfRendererSeparatedProject/PdfRendererSeparatedProject/OBJECT_OVERLAY_WORKLOG.md)
- [VIEWER_UI_WORKLOG.md](/D:/Projects/C%23/PdfRendererSeparatedProject/PdfRendererSeparatedProject/VIEWER_UI_WORKLOG.md)

## 8. Viewer UI checkpoint

Текущий viewer-side checkpoint такой:

- progress bar при открытии PDF уже вынесен в отдельную верхнюю loading-strip
  панель;
- thumbnail strip уже умеет адаптировать размер миниатюр под ширину панели,
  но его scroll/paint стабильность всё ещё требует добивки;
- `PdfDocumentView` больше не должен опираться на giant `AutoScroll` surface:
  в коде уже внедрена внутренняя scroll-позиция и собственные `VScrollBar` /
  `HScrollBar`;
- в режиме постраничной прокрутки исправлено двойное срабатывание колеса мыши,
  из-за которого один notch раньше перескакивал сразу через две страницы;
- этот шаг сделан именно для устранения практического лимита long smooth
  scroll, когда `poradnik2013.pdf` переставал нормально листаться примерно
  около страницы `18/19`;
- код успешно собирается, но финальный статус этого конкретного viewer fix
  нужно подтвердить живой проверкой обновлённого приложения.
