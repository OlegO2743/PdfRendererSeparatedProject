# PdfRendererSeparatedProject

## Что уже поддерживается (стабильная база)

### Структура PDF
- обычные indirect objects `N 0 obj ... endobj`
- stream objects
- `/Length` прямой и косвенный
- `FlateDecode`
- первая и последующие страницы (простое извлечение всех `/Type /Page`)
- `/Contents N 0 R`
- `/Contents [ ... ]`
- `/Resources`
- `/Font`
- `/XObject`
- `/Resources /ColorSpace`

### Graphics state
- `q`
- `Q`
- `cm`
- `w`
- `RG`
- `rg`
- `K`
- `k`

### Path
- `m`
- `l`
- `c`
- `v`
- `y`
- `h`
- `re`
- `S`
- `s`
- `f`
- `f*`
- `B`
- `B*`
- `b`
- `b*`
- `W`
- `W*`
- `n`

### Текст
- `BT`
- `ET`
- `Tf`
- `Tm`
- `Td`
- `TD`
- `T*`
- `Tc`
- `Tw`
- `Tz`
- `TL`
- `Ts`
- `Tj`
- `TJ`
- `'`
- `"`

### XObject
- `Do` для `Form XObject`
- `Do` для `Image XObject`
- вложенный `Image XObject` внутри `Form XObject`

### Color spaces
- `/DeviceGray`
- `/DeviceRGB`
- `/DeviceCMYK`
- `ICCBased` phase 1 fallback через `Alternate`
- именованные ColorSpace из `/Resources /ColorSpace`

## Что ещё предстоит
- настоящий ICC color management для совпадения оттенков с браузером
- `Indexed`
- `DeviceN`
- `Separation`
- soft mask / transparency / blend modes
- `JPXDecode`
- `CCITTFaxDecode`
- `JBIG2Decode`
- Type0 / CID fonts / CMap / ToUnicode
- точный page tree order
- object streams / xref streams / incremental update
- аннотации / формы / ссылки
- predictors для изображений
- image masks и soft masks
- более точная метрика текста

## Разделение Stable / Experimental
- `PdfCore/Color/Stable/*` — стабильная база
- `PdfCore/Color/Experimental/*` — экспериментальный ICC phase 2 слой
- переключение режима через UI:
  - `Stable phase 1`
  - `Experimental ICC phase 2`

## UI
- zoom in / zoom out
- fit width
- горизонтальная и вертикальная прокрутка (через AutoScroll)
- переход по страницам
- demo-кнопки для всех сценариев

## Важно
Старые demo не должны редактироваться ради экспериментов.
Для новых тестов добавляйте только новые методы в `SamplePdfFactory.cs`.
