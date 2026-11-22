# Бенчмарк дня 1 — Первый запрос к LLM через API

Обновлено: 22.11.2026. Реализация: VERIFIED_OFFLINE. Статус дня: DONE_VERIFIED. Scenario/scorer: 1/1.
Источник: D:/Codex/MyProjects/UniversalAI/DAYS.md; прочитан 22.11.2026; SHA256 раздела d83fac8d816b5a1727322c2a7f62b39336ac77cda1f87ed95158fbfd87980fd3.
Карточка: [Day-01](D:/Codex/MyProjects/UniversalAI/Tasks/Day-01/TASK.MD).

## Исходное задание (дословно)

День 1. Первый запрос к LLM через API

Напишите минимальный код, который:
👉 отправляет запрос в LLM через API
👉 получает ответ
👉 выводит его в консоль или простой интерфейс (CLI / Web)

Результат:
Код, который отправляет запрос в LLM через API и получает ответ

## Методика и соответствие заданию

Один запрос «2+2» через реальный provider, вывод ответа в журнал/интерфейс, сохранение запроса/ответа/usage; отдельная проверка ответа 4.

## Запуск

dotnet run --project UniversalAI.csproj -- --benchmark day1 --provider proxyapi --auto

Веб: /experiments → day1 → полный исходник и сценарий → запуск. Просмотр не создаёт RunId и не вызывает API. Offline: dotnet run --project tests/UniversalAI.Checks. Полный сценарий — по умолчанию; сокращённые исследовательские режимы не являются полной приёмкой.

## Артефакты и оценивание

Отдельный RunId: UNIVERSALAI_DATA_DIR/runs/<id>/. Метаданные, версии, pricing.json, output.log; в artifacts — report.*, api-requests.jsonl, api-calls.jsonl, полный experiment-materials.json; новые сценарии сохраняют experiment-view.json. Сырой ответ/ошибка и usage не подменяются. Локальная оценка токенов отличается от usage API. Неизвестная стоимость — Н/д, не ноль. Ошибка/отмена и неуспешная оценка ответа различаются; подробности scorer находятся в коде и методике выше.

## Проверки и реальное измерение

COMPLETED 22.11.2026; RunId b9cec8cd37474cabbcfcafcc69d805d8; deepseek/deepseek-v4.1-flash; 1 API-попыток; проверки 1/1; usage известных ответов 45 входных + 39 выходных. Каталог D:/UniversalAI/UniversalAI/.local/course-measurements/runs/b9cec8cd37474cabbcfcafcc69d805d8.

Проверено 22.11.2026: dotnet build UniversalAI.sln --no-restore (0 ошибок/предупреждений); все UniversalAI.Checks (.local/course-checks-final.log); self-test Day9/Day10 (.local/course-day9-selftest.log, .local/course-day10-selftest.log). Verify-CourseWeb.ps1: числовая сортировка API/HTML, все 15 полных заданий без запуска, пять новых сценариев и настоящий перезапуск тестового серверного процесса с сохранением tool calls/results. Журнал .local/course-web.log; артефакты .local/checks/course-web-49642077833d4e698b3462b830b04a35/.

В браузере проверены порядок day1–day15 и полный текст дня 2 в подготовке; снимок просмотрен. Мобильный viewport не проверялся. Использовались отдельные каталоги и процессы; основная БД/сервер пользователя не затрагивались. Сетевая/веб-проверка выполнена вне песочницы из-за сетевых ограничений и Windows DPAPI. Предыдущие неудачные проверки сохранены в журналах. Commit/push не выполнялись.

## История и ограничения

Предыдущая подробная методика и исторические результаты: [архив паспорта](D:/Codex/MyProjects/UniversalAI/History/2026-11-22-before-course-unification/Benchmarks/BENCHMARK-DAY-1.md). Исторические результаты в runs/results не менялись. Основные данные пользователя не использовались. Общая матрица: [BENCHMARKS.md](D:/Codex/MyProjects/UniversalAI/BENCHMARKS.md).
