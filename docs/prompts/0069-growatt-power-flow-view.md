# Задача: Нов изглед „Поток“ (Power flow) за страницата Live Growatt

Date: 2026-11-27

## Original prompt

Задача: Нов изглед „Поток“ (Power flow) за страницата Live Growatt

КОНТЕКСТ
Страницата Live Growatt (/properties/:id/growatt) показва master и slave инвертор и споделена батерия. Искам нов визуален изглед „Поток“ с илюстрации, подобен на енергийната схема в приложението ShinePhone/Growatt. Сегашният изглед остава НАПЪЛНО непроменен и е достъпен като „Детайли“.

Първо разгледай кода: компонента на Growatt страницата, откъде идват данните (API/hooks/типове), как работят live updates и какви UI компоненти и стилове вече се ползват (например превключвателя Compact/Detailed на Dashboard). Следвай съществуващите конвенции. Не добавяй нови зависимости, ако не е наложително.

Вече работи и трябва да се преизползва, не да се пише наново:
- Обобщението „Solar installation“ сумира master и slave: PV production, Inverter output, AC input supply (наличие + напрежение), AC input power.
- Статусът на инверторите е „Online from recent readings“ (Fresh/Stale), а облачният offline флаг на Growatt е отделен.
- Логиката „No PV generation (0 V)“.

1. ПРЕВКЛЮЧВАТЕЛ НА ИЗГЛЕДИТЕ
- Segmented control „Поток | Детайли“ горе вдясно на страницата, в стила на Compact/Detailed от Dashboard.
- По подразбиране: „Поток“.
- Изборът се пази в localStorage и се отразява в URL (?view=flow / ?view=details).
- „Детайли“ рендира СЕГАШНИЯ компонент без промени.
- Двата изгледа ползват едни и същи данни и live updates, без двойно извличане.

2. ИЗГЛЕД „ПОТОК“: ЕНЕРГИЙНА СХЕМА
Кръстообразна подредба:
- горе: Солар (слънчев панел)
- ляво: Мрежа (електрически стълб)
- център: Инвертор
- дясно: Дом (къща)
- долу: Батерия
Всеки възел е кръгъл „медальон“ (около 96 px, на мобилен по-малък) с илюстрация вътре и надпис със стойност под него.

Илюстрации:
- Направи СОБСТВЕНИ inline SVG илюстрации: слънчев панел, стълб, инвертор, къща, батерия. Стил: flat/леко обемен, с меки градиенти.
- НЕ копирай иконите или графиките на Growatt. Трябва да са оригинални.
- Сложи ги като отделни компоненти в съществуващата структура (например components/growatt/illustrations/), с aria-hidden и текстов етикет до тях.
- Батерията показва нивото на заряд като запълване вътре в илюстрацията и мълния, когато се зарежда.
- Слънчевият панел е сив/приглушен, когато няма PV производство (по съществуващата логика „No PV generation“).

Линии между възлите:
- Шеврони по посоката на потока: мрежа→инвертор, солар→инвертор, инвертор→дом, инвертор↔батерия (посоката зависи от заряд/разряд).
- Ако има поток, шевроните са анимирани по посоката и оцветени по източника: мрежа синьо, солар кехлибарено, батерия зелено, дом неутрално. Без поток: статична пунктирана сива линия.
- Уважавай prefers-reduced-motion: тогава само статични стрелки.
- Стойността стои до всяка линия (например „387 W“).

Надписи под възлите:
- Солар: обща PV мощност. Без производство: „Няма производство“.
- Мрежа: „Внос: X W“ (AC input power) + напрежение и честота.
- Инвертор: режим на работа (например „AC charge & Bypass“) + натоварване на master/slave в %.
- Дом: Inverter output в W. Ако има налична привидна мощност (VA), покажи „W / VA“.
- Батерия: %, напрежение, „Зарежда X W“ / „Разрежда X W“ / „В покой“.

3. БАТЕРИЯ И ENERGY (останали проблеми)
- Споделената батерия показва „Shared battery flow unavailable“, въпреки че master/slave имат „Battery charge/discharge“ (например master Charging 63 W, slave Idle 0 W). Провери защо. Ако е бъг, оправи го. Ако е нарочно, в новия изглед покажи сумата от инверторите с бележка „по данни от инверторите“. Логиката за сумиране е чиста функция с тестове.
- Отделна задача: страницата Energy още казва „No provider readings were recorded in this range“ за PV и консумация, макар обобщението на Growatt да има стойности. Графиката на батерията работи. Провери дали тези показания се записват в историята и оправи това в отделна, ясно описана промяна.

4. СТАТУС НА ДАННИТЕ
- Горе в новия изглед: един общ бадж по съществуващата Fresh/Stale логика: „Получава данни · HH:mm“ (зелен), „Остарели данни · HH:mm“ (жълт) или „Няма данни“ (червен). Час = последното отчитане от източника.
- Облачният offline флаг на Growatt: малък сив етикет „Облак: offline“ с tooltip с обяснение, а не основен статус.

5. РЕД „ДНЕС“ ПОД СХЕМАТА
Четири малки карти: Произведено от солар (kWh), Консумация (kWh), Разредено от батерия (kWh), Дял на солара (% от консумацията). Под всяка: разбивка master + slave. Ако стойността липсва: „—“.

6. КРАТКИ КАРТИ НА ИНВЕРТОРИТЕ
За всеки инвертор: име, статус, 3 основни числа (товар, внос от мрежата, батерия), лента за натоварване в % и разгъваемо „Всички показания“ с останалите стойности.

7. ДИЗАЙН И ДОСТЪПНОСТ
- Светлата тема и токените/цветовете на приложението. Схемата може да е в карта с лек тъмен/градиентен фон за контраст, но да изглежда част от приложението.
- Responsive: под около 640 px схемата се подрежда вертикално или се мащабира, без хоризонтален скрол.
- Контраст на текста поне 4.5:1. Превключвателят е истински бутон, достъпен с клавиатура.
- Стойностите се обновяват на място с live updates, без примигване на целия компонент.

8. ТЕСТОВЕ И ПРОВЕРКА
- Unit тестове за потока на батерията: и двата инвертора налични, само единият, никой, заряд/разряд/покой.
- Тест, че превключвателят запазва избора (localStorage + URL) и че „Детайли“ рендира сегашния компонент.
- Пусни lint, type-check, тестовете и build-а и оправи грешките.

ОГРАНИЧЕНИЯ
- Не пипай управление на релета, температури или каквито и да е команди към устройства.
- Не променяй сегашния изглед „Детайли“ освен ако поправката на батерията не го изисква. Тогава го опиши.
- Не прави commit/push. Накрая дай кратко резюме: променени файлове, какво е оправено за батерията и Energy и какво остава отворено.

## Follow-up from the user (same task)

> можеш накрая да комитнеш без да пушваш

The user later allowed a local commit, replacing the original "Не прави commit/push". Nothing was pushed.

## Execution summary

### Findings

- **Shared battery.** *Shared battery flow unavailable* is by design. Growatt reports `pBat` per inverter, and the adapter keeps that flow on the reporting inverter so it is never counted twice (see the Growatt guide). The shared battery device carries only SOC and voltage. The Details view is therefore unchanged. The power-flow view shows the inverters' sum, computed by `batteryFlow` and labelled *по данни от инверторите*.
- **Energy.** The PV and consumption readings are recorded, but per inverter: the `PvPower` and `ActivePower` series of each inverter endpoint. The page asked for installation-level `pvPower` and `householdPower` series, which Growatt V4 never records. This is the same root cause as the summary in prompt 0068. The battery chart worked because it uses an inverter endpoint.

### Changes (frontend only)

- **New view** in `features/growatt/`:
  - `GrowattFlow.tsx`: the view, loaded lazily.
  - `flow.ts`: pure helpers `batteryFlow`, `dailyTotal`, `solarShare`, `dataStatus`, `cloudOffline` and `readingOf`.
  - `illustrations/`: `SolarPanel`, `GridTower`, `InverterBox`, `House`, `Battery` and `svgId`.
- **`GrowattPanel.tsx`:** the **Поток | Детайли** switch (`GrowattViewToggle`, `?view=` and localStorage), with one query for both views. `GrowattSummary` (Details) is unchanged.
- **`Energy.tsx`:** `sumSeries`, which adds separate meters at matching times and leaves gaps otherwise, and `productionCurves`, which prefers installation series and otherwise sums the inverters. Summed curves request each endpoint once.
- **`styles.css`:** the diagram, chevron animation with reduced-motion handling, the two-column layout below 640 px, and the today and inverter cards.
- **Tests:** `features/growatt/flow.test.ts` (9) and `features/growatt/GrowattFlow.test.tsx` (7).
- **Docs:** architecture 18 and 03, the Growatt guide and the handoff.

### Verification

- **Build and tests:** the type-check and production build passed, and Vitest reported 173 passes, 16 of them new.
- **Bundle:** the power-flow view is a separate 18 kB chunk. The main chunk is 433 kB, and the earlier size warning is gone.
- **Lint:** the project has no lint script.
- **Backend:** unchanged and not rerun.
- **Not touched:** databases, credentials, OAuth, relays, temperatures, and every device command path.

### Open items

- **Apparent power:** no VA metric is mapped, so Home shows W only.
- **Language:** the view is in Bulgarian while the rest of the application is in English; there is no translation layer.
- **Not checked visually:** the layout has not been checked in a real browser at phone widths or with live Growatt data.
- **Solar share:** it is an indication (PV ÷ output, capped at 100 %), not an energy balance.
- **Energy chart:** it shows raw readings, so a 1 W night reading appears as 1 W.
