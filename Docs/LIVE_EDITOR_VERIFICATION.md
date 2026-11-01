# Офлайн-проверка Live Editor, 1 ноября 2026

**Приёмка полного ТЗ не закрыта.** Результаты относятся к реализованному срезу, описанному в LIVE_EDITOR_ARCHITECTURE.md. Игра и dedicated server не запускались; native физика, несколько GM и поздний вход не испытаны.

## Воспроизведение

Из каталога игры:

```powershell
& 'LocalMods/Primary package/Tools/verify_live_editor.ps1'
```

Скрипт собирает инструменты и моды, исполняет связанные с production-кодом тесты, проверяет метаданные и codec установленных сборок, запускает статические аудиты и git diff --check. Он не вызывает entry point игры или dedicated server. На чистой машине проекты .NET сначала требуют обычного restore SDK; параметры GameRoot/LuaCsRoot можно передать явно.

Сырые логи, DLL проверки, карта исходников и хеши:

`C:/Steam/steamapps/common/Barotrauma/Docs/PrimaryPackage/LiveEditor/`

`verification.json` связывает отчёты с SHA-256 исходников production, тестов, инструментов и собранных DLL. Папка original-content содержит исходные тексты трёх изменённых файлов SubSeal перед правкой. Это не резервная копия всего мода.

## Подтверждённые результаты

| Статус | Проверка | Результат и предел доказательства |
|---|---|---|
| PASS | BuildSimulator Primary | client 457 файлов, server 283 файла; оба SUCCEEDED. Shared — штатный диагностический-only проход с ошибками, не отдельная сборка LuaCs |
| PASS | contentclient | 17 клиентских файлов SUCCEEDED; серверная цель пуста по RunConfig |
| PASS | contentserver | 17 серверных файлов SUCCEEDED; клиентская цель пуста по RunConfig |
| PASS | GMPlatform | 33 721 проверка/численная выборка, 0 ошибок; сцены, движение фантомов, эмоции, журнал мира, AABB, визуальная математика |
| PASS | Нативные схемы | На каждой стороне 73 одинаковых свойства; 219 недопустимых значений отклонены. Ни один реальный игровой setter этим тестом не исполнялся |
| PASS | Native codec | SceneWire 1601 бит и 201 отклонённое побайтовое усечение; GMAnnouncement 896 бит и 112 отклонённых усечений на каждой стороне |
| PASS | Тип сообщения | Значение ServerMessageBoxInGame проверено по установленной сборке: 11; это отдельный путь от ScriptToast |
| PASS | Система координат | 6 ABI/parser случаев для frame Guid и позиции; вне диапазона, дробные координаты, NaN и пустой frame отклонены |
| PASS | ContentContracts | Linked production Entitlement/ResidentAsset/LoadPatches/SubmarineSkeleton с заменённым engine/file adapter; проверка active claim, защиты чужих файлов, failed restore и происхождения XML |
| PASS | Аудиты | 30 хешированных source paths движка, статические guardrails; 18 эмоций и 25 неизменённых OGG; git diff --check |
| FINDING | Активный contentclient | В профиле используется Workshop contentclientfixed 3831040655. Локальное исправление ещё не является исправлением активной клиентской копии |
| NOT_RUN | Рендер | Скриншотов production GUI/скримеров нет. Нельзя подтвердить визуальное качество, clipping, контраст, LOS и GPU-стоимость |
| NOT_RUN | Native симуляция/сеть | Нет результата живой физики, MidRoundSync, доставки, jitter/loss/reorder, reconnect или host-client |

33 721 — число assertions и численных выборок, не число отдельных игровых сценариев. ContentContracts не проверяет реальную криптографию/передачу файла. Native contracts загружают метаданные DLL и вызывают codecs; плагины и игровой мир не инициализируются.

## Регрессии текущего журнала

- Разные экземпляры одинакового компонента и два независимых GM сохраняют обе правки.
- Повтор запроса не исполняется заново; другой автор не может использовать тот же request ID.
- Устаревшие ревизии, изменённое симуляцией значение и несуществующая инкарнация отвергаются до записи.
- Неудачная валидация/атомарный отказ adapter не публикуют новую ревизию.
- Undo не отменяет другую независимую правку и не перезаписывает более позднюю операцию A→B→A.
- Redo получает новую ревизию; повтор исходного Apply после Undo не выполняет его заново.
- Неизменяемая история сохраняет Apply/Undo/Redo и исходное время.
- Лимит истории проверяется до изменения; при завершении раунда владения очищаются.

## BENCH

Измерение чистого production-индекса на одном потоке, Release, после прогрева, 100 000 итераций:

| Сценарий | Измерено |
|---|---:|
| Построение индекса 20 000 синтетических прямоугольников | 17,9105 мс; 5 545 480 выделенных байт, включая входные объекты/буферы |
| Локальный AABB-запрос к этому индексу | 3,092893 мкс/запрос; 0 байт GC текущего потока |
| Переход одного объекта между свободными клетками с повторным использованием buckets | 0,19898 мкс/обновление; 0 байт после прогрева |
| 32 ожидающие сцены с тестовым миром | 0,833178 мкс/обновление всех сцен; 0 байт после прогрева |
| Образец GMAnnouncement в native codec | 896 бит / 112 байт содержимого; без внешнего framing и transport overhead |

Это не сравнение «до/после», не FPS, не полная стоимость выделения в движке и не замер реального сетевого трафика. CPU/GC native adapters, GPU, working set игрового процесса, задержка редактирования, массовое создание и восстановление динамического мира остаются NOT_RUN. Размер одного codec-образца не является оценкой всей сетевой нагрузки.

## Файлы и границы результата

| Пункт запроса | Основные файлы | Что проверено / что осталось |
|---|---|---|
| Исследование | Tools/audit_live_editor.py, Docs/LIVE_EDITOR_ARCHITECTURE.md | Source anchors, хеши, реальные зависимости; не установлена причина конкретной старой сессии без её логов |
| Универсальный редактор | Shared/Interactions/DeviceEditor.cs; Client/Interactions/DeviceView.cs | Native metadata/typed parsing, сборка; game setters NOT_RUN |
| Выделение | Shared/Interactions/BoundsIndex.cs, WorldQueryService.cs; Client/Interactions/LiveEditor.cs | Randomized AABB против brute force, большие объекты, отрицательные координаты, удаление и обновление; UI NOT_RUN |
| Совместные операции | Shared/Interactions/WorldEdits.cs; Server/Interactions/DeviceServer.cs | Production CAS/Undo-регрессии; конкуренция реальных GM NOT_RUN; структурных транзакций нет |
| Перемещение | Shared/Interactions/LiveItemTransform.cs | ABI/frame parser/ограничения и сборка; физика NOT_RUN; fixtures/провода/стыковка/контейнеры исключены |
| Поздний вход | DeviceServer.cs, DeviceClient.cs, WorldCapabilities.cs | Состояние готовности, object binding, бюджетирование, capability guard; native порядок/reconnect NOT_RUN |
| SpawnSub | Только архитектурное исследование | FAIL: исполнитель и pre-native baseline отсутствуют |
| Защищённый контент | contentserver/Entitlement.cs; contentclient/LoadPatches.cs, ResidentAsset.cs | Production decision tests и сборки; реальный transfer/decrypt/join NOT_RUN; активная Workshop-копия не изменена |
| F3 | GMAnnouncement.cs; клиентские/серверные GMAnnouncements.cs, GMMessageView.cs; GUIInput.cs | Codec, native enum, серверные права; палитра/показ/адресная доставка NOT_RUN |
| Возрождение | Server/Interactions/PlayerRevival.cs; Client/Interactions/PlayerRevival.cs | Сборка и ABI native вызовов. Сохранение личности/кошелька, управление и правила режима в игре NOT_RUN |
| Скримеры | HorrorSignal.cs; HorrorEditor.cs; DirectorPlayback.cs; PhantomRenderer.cs | Детерминизм/границы visual math, lifecycle audit и сборка; вид/звук/LOS/GPU NOT_RUN |
| Сохранение | Server/Interactions/WorldEditExport.cs | Отдельный ограниченный audit export; filesystem/runtime action NOT_RUN. Blueprint/preset restore FAIL |

Пути в таблице относительно CSharp, если не указаны Tools/Docs/contentclient/contentserver. Полный текущий перечень production-файлов и хешей — verification.json.

## Заменённые механизмы

Ручные DeviceEditor.Fields и байтовые ID 29 настроек заменены политикой метаданных. ID+prefab как единственная проверка реплики заменён закреплённой инкарнацией. Очередь «дождаться сущности за 30 секунд» заменена readiness/bind/ack без переноса старой дельты на новый объект. Полные повторные обновления инспектора заменены наблюдением изменений и отдельными snapshot chunks. Открытие окна больше не отменяет persistent edit overlay. Сохранение журнала отделено от записи исходных .sub.

Обычные administrative EntityActions и сценовые действия сохранены. Они не переименованы в строительный редактор и не считаются закрытием create/clone/delete/topology/SpawnSub.

## Обязательные игровые испытания — все NOT_RUN

1. Клиент и dedicated server с согласованным v4: вход, host-client, обычный персонаж и наблюдатель; F2/F3/F4, чат/голос, Escape, разные разрешения и масштаб UI.
2. Дверь: выбрать непосредственно в мире, открыть/закрыть, stuck/broken/status effects, два одинаковых компонента, обновление значения без потери ввода, конфликт двух GM, отзыв прав.
3. Разрешённое оборудование: перемещение на движущейся подлодке и между hull одной подлодки; preview без сетевого спама; отказ при StaticFixtures, wires, активном теле и занятой области; смена frame во время ожидания ответа.
4. Нативное удаление/повторное использование ID тем же prefab между offer, ack и delta; отключение на каждом этапе; отсутствие зависания очереди на недоступной цели.
5. Поздний вход после большого числа разрешённых правок; изменения во время native sync, readiness race, повторный вход и неподдерживаемая версия. Проверить окончание MidRoundSync и сообщения о несовместимости.
6. Undo/Redo: авторство, stale revision, поздняя запись другого GM, естественное изменение устройства и недоступная цель; экспорт audit и неизменность исходных .sub по хешам.
7. F3: RGBA, сохранённые цвета, предпросмотр всех форматов; тип 11 отдельно от ScriptToast; global/addressed; игрок в lobby/late sync; отсутствие глобального изменения настроек чата.
8. Возрождение живого, мёртвого, gib и удалённого тела; повторный клик, параллельное обычное возрождение, PvP team, кампания, permadeath и отдельное разрешение; одна личность/одно управление, кошелёк/таланты/камера.
9. Каждый horror preset: личный адресат, GM preview, наблюдатель, остановка одного/всех эффектов, отключение, завершение раунда, отсутствие native Character/AI/physics у фантома.
10. После реализации структурного слоя: полный набор create/change/clone/delete стен, hull/gap, doors, wires и docking; физические последствия, вода/кислород, AI и свет; атомарные конфликтующие группы.
11. После реализации SpawnSub: одна/несколько подлодок, inventory/connections, движение, стыковка, damage/repair/remove; новый клиент до/во время/после каждой операции; проверка полного графа ID и native events.
12. Network impairment: latency/jitter/loss/reorder, timeout, backlog overflow, reconnect; CPU/GPU/GC/working set и wire traffic под массовыми операциями. Синтетические текущие тесты не заменяют эту матрицу.

Полный строительный редактор, безопасное структурное редактирование и SpawnSub остаются **FAIL по наличию реализации**, а не «готово, осталось проверить». Запрет запуска блокирует игровую приёмку, но не является объяснением отсутствующих исполнителей.
