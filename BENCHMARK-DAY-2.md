# Бенчмарк дня 2 — Формат ответа

Обновлено: 22.11.2026. Реализация: VERIFIED_OFFLINE. Статус дня: DONE_VERIFIED. Scenario/scorer: 1/1.
Источник: D:/Codex/MyProjects/UniversalAI/DAYS.md; прочитан 22.11.2026; SHA256 раздела 5d4f8e71585ce92cf824faf640274160fcb1fadd71d7c4d0ff5c0a64e0136726.
Карточка: [Day-02](D:/Codex/MyProjects/UniversalAI/Tasks/Day-02/TASK.MD).

## Исходное задание (дословно)

День 2. Формат ответа

Отправьте один и тот же запрос, но:
👉 добавьте явное описание формата ответа
👉 ограничьте объём ответа (`max_tokens`)
👉 добавьте stop sequence
👉 задайте строгую схему ответа (JSON Schema / structured output)

Прогоните каждый вариант несколько раз.

Сравните ответы:
👉 без ограничений
👉 с ограничениями
👉 со строгой схемой
👉 проверьте, можно ли распарсить ответ кодом

Результат:
Один и тот же запрос с разным уровнем контроля + вывод, какой способ ломается и на чём

## Методика и соответствие заданию

Один вопрос о трёх цветах светофора; 6 режимов × 3 повтора = 18 запросов: baseline, явный JSON-формат, max_tokens=16, stop КОНЕЦ, response_format json_schema strict=true и комбинация. Все строки парсятся кодом и проверяются по структуре; режимы сравниваются. Исходная schema и настройки сохранены. Нарушение формата/обрезка — результат эксперимента, а не скрываемая ошибка. Неподдерживаемый endpoint фиксируется, схема не заменяется промптом.

## Запуск

dotnet run --project UniversalAI.csproj -- --benchmark day2 --provider proxyapi --auto

Веб: /experiments → day2 → полный исходник и сценарий → запуск. Просмотр не создаёт RunId и не вызывает API. Offline: dotnet run --project tests/UniversalAI.Checks. Полный сценарий — по умолчанию; сокращённые исследовательские режимы не являются полной приёмкой.

## Артефакты и оценивание

Отдельный RunId: UNIVERSALAI_DATA_DIR/runs/<id>/. Метаданные, версии, pricing.json, output.log; в artifacts — report.*, api-requests.jsonl, api-calls.jsonl, полный experiment-materials.json; новые сценарии сохраняют experiment-view.json. Сырой ответ/ошибка и usage не подменяются. Локальная оценка токенов отличается от usage API. Неизвестная стоимость — Н/д, не ноль. Ошибка/отмена и неуспешная оценка ответа различаются; подробности scorer находятся в коде и методике выше.

## Проверки и реальное измерение

COMPLETED 22.11.2026; RunId b90a00fffbec459d8dd09e7062eee570; openai/gpt-4o-mini; 18 API-попыток; проверки 7/18; usage известных ответов 1353 входных + 1353 выходных. Каталог D:/UniversalAI/UniversalAI/.local/course-measurements/runs/b90a00fffbec459d8dd09e7062eee570. JSON/структура: baseline 0/3, формат 3/3, лимит 0/3, stop 1/3, schema 3/3, combined 0/3. Ожидаемые поломки формата — измеренный результат, а не незавершённый протокол.

Проверено 22.11.2026: dotnet build UniversalAI.sln --no-restore (0 ошибок/предупреждений); все UniversalAI.Checks (.local/course-checks-final.log); self-test Day9/Day10 (.local/course-day9-selftest.log, .local/course-day10-selftest.log). Verify-CourseWeb.ps1: числовая сортировка API/HTML, все 15 полных заданий без запуска, пять новых сценариев и настоящий перезапуск тестового серверного процесса с сохранением tool calls/results. Журнал .local/course-web.log; артефакты .local/checks/course-web-49642077833d4e698b3462b830b04a35/.

В браузере проверены порядок day1–day15 и полный текст дня 2 в подготовке; снимок просмотрен. Мобильный viewport не проверялся. Использовались отдельные каталоги и процессы; основная БД/сервер пользователя не затрагивались. Сетевая/веб-проверка выполнена вне песочницы из-за сетевых ограничений и Windows DPAPI. Предыдущие неудачные проверки сохранены в журналах. Commit/push не выполнялись.

## История и ограничения

Предыдущая подробная методика и исторические результаты: [архив паспорта](D:/Codex/MyProjects/UniversalAI/History/2026-11-22-before-course-unification/Benchmarks/BENCHMARK-DAY-2.md). Исторические результаты в runs/results не менялись. Основные данные пользователя не использовались. Общая матрица: [BENCHMARKS.md](D:/Codex/MyProjects/UniversalAI/BENCHMARKS.md).
