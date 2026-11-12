# Distributed TSP Solver (Data-Parallel Genetic Algorithm)

[English](#english-version) | [Русский](#русская-версия)

---

## English Version

# Distributed Traveling Salesperson Problem (TSP) Solver

This project solves the Traveling Salesperson Problem (TSP) using a high-performance, deterministic Genetic Algorithm (GA) distributed across independent ASP.NET Core REST API worker nodes. Unlike traditional island models, this system uses **generation-level data parallelism (slice distribution)**, guaranteeing **bit-exact reproducibility** identical to a single-threaded local solver.

## Solution Structure

The solution consists of 4 projects:

1. **commivoy** — Core Shared Class Library (.NET).
   * `GeneticSolver`: Implements the GA core using flat 1D distance arrays (`double[]`), tournament selection, Order Crossover (`OX1`), swap mutation, `2-Opt` local search optimization, and multi-core execution via `Parallel.For` with `Partitioner`.
   * `FastRandom`: Stack-allocated, stateless deterministic PRNG bound to `(MasterSeed, IndividualIndex, Generation)`, eliminating thread contention and guaranteeing identical results regardless of thread or node count.
   * `MatrixGenerator`: Generates deterministic city coordinates and Euclidean distance matrices from a given seed.
   * `SliceTaskRequest` / `SliceTaskResult`: Data Transfer Objects (DTOs) for distributed slice computation.

2. **commivoy_service** — Computational ASP.NET Core Web API Worker Service.
   * Exposes health-check and slice-evolution endpoints (`/api/Ga/ping` and `/api/Ga/evolve-slice`).
   * Caches distance matrices in memory by `TaskId` to avoid redundant network serialization on every generation.
   * Handles client disconnects and `OperationCanceledException` gracefully without crashing the host process.

3. **commivoy_app** — WPF Client Desktop Application.
   * Acts as the Master coordinator: manages worker endpoints, checks node availability, and splits each generation's population into slices across active nodes.
   * Runs coordination asynchronously on a background thread to keep the UI responsive.
   * Visualizes city coordinates, the best discovered tour, and real-time convergence charts.

4. **commivoy_service.Tests** — Unit & Integration Testing Project (xUnit).
   * `AlgorithmTests` & `AdvancedAlgorithmTests`: Unit tests verifying path length math, permutation integrity, `FastRandom` uniform distribution, `2-Opt` intersection uncrossing, `CancellationToken` responsiveness, and single-thread vs. multi-thread bit-exact equivalence.
   * `GaControllerTests`: End-to-end API integration tests using `WebApplicationFactory<Program>` (`Microsoft.AspNetCore.Mvc.Testing`) running in-memory without binding physical network ports.

---

## System Architecture & Key Features

### 1. Data-Parallel Slice Distribution & Bit-Exact Determinism
Instead of evolving isolated populations, the Master client coordinates the population synchronously across generations:
* Elitism preserves the top individuals locally.
* The remaining offspring indices `[EliteCount .. PopulationSize)` are divided into slices and dispatched concurrently via HTTP POST to all active worker nodes.
* Because `FastRandom` computes random numbers purely from `(MasterSeed, IndividualIndex, Generation)`, splitting a generation across 1, 2, or 10 nodes produces the **exact same offspring and route length** as running locally on a single thread.

### 2. Distance Matrix Caching & Fault Recovery (`409 Conflict`)
* On Generation `0`, the client sends the full distance matrix along with a unique `TaskId`. The worker caches this matrix in RAM.
* On subsequent generations (`Generation > 0`), the client sends `Distances = null`, drastically reducing JSON payload size and network overhead.
* If a worker node restarts or a new node joins mid-run without the cached matrix, it responds with **`409 Conflict`**. The client catches this status and automatically retries the request with the full matrix attached.

### 3. High-Performance Execution (`Release` Mode)
* **Flat Memory Layout**: 2D matrices are flattened into contiguous `double[]` arrays to maximize CPU cache locality during `2-Opt` and fitness evaluations.
* **Minimal Logging Overhead**: Running in `Release` configuration suppresses verbose per-request console I/O, ensuring CPU cycles are spent on computation rather than console rendering.

---

## API Documentation (`commivoy_service`)

### 1. Health Check / Initialization Ping
`GET /api/Ga/ping`

Returns `200 OK` with a status message confirming the node is online and ready to accept tasks.

### 2. Evolve Population Slice
`POST /api/Ga/evolve-slice`

**Request Body (`SliceTaskRequest`):**
```json
{
  "taskId": "a1b2c3d4e5f6...",
  "distances": [
    [0.0, 10.5, 15.2],
    [10.5, 0.0, 20.1],
    [15.2, 20.1, 0.0]
  ],
  "population": [
    [0, 1, 2],
    [2, 1, 0]
  ],
  "fitness": [45.8, 45.8],
  "masterSeed": 42,
  "generation": 0,
  "startIndex": 2,
  "endIndex": 10,
  "mutationProb": 0.2,
  "threads": 4
}
```
*(Note: `distances` is `null` for generations $> 0$ once cached on the worker.)*

**Response Body (`SliceTaskResult`):**
```json
{
  "offspring": [
    [0, 2, 1],
    [1, 0, 2]
  ],
  "offspringFitness": [45.8, 45.8]
}
```

**HTTP Status Codes:**
* `200 OK`: Slice successfully evolved (returns empty arrays if `startIndex >= endIndex`).
* `400 Bad Request`: Invalid payload or dimension mismatch between matrix size and route length.
* `409 Conflict`: `distances` was `null`, and `taskId` was not found in the worker's memory cache.

---

## Setup and Running Instructions

### 1. Starting Compute Nodes via CMD
Always launch worker services in **`Release`** mode for maximum computational throughput:

1. Open the **`commivoy_service`** project folder in File Explorer.
2. Click the address bar, type `cmd`, and press **Enter** to open Command Prompt directly in that directory.
3. Run the service on your desired port:
   ```cmd
   dotnet run -c Release --urls "http://localhost:5001"
   ```
4. To simulate multiple cluster nodes on one machine, open additional `cmd` windows from the `commivoy_service` folder and launch them on different ports:
   ```cmd
   dotnet run -c Release --urls "http://localhost:5002"
   ```

### 2. What Appears in the Console Window
When a worker starts and processes tasks, you will observe the following in the CMD window:
* **Startup**: Kestrel initialization messages showing the active listening address (e.g., `Now listening on: http://localhost:5001`) and `Application started. Press Ctrl+C to shut down.`
* **During Computation**: In `Release` mode, routine HTTP logs are minimized so console I/O does not bottleneck CPU performance. You will see brief startup/caching notices or minimal request activity while the CPU cores execute `Parallel.For`.
* **On Client Stop / Cancellation**: If you click **Stop** in the client app mid-calculation, the HTTP connection aborts; the console may display a handled cancellation notice (`Request aborted / OperationCanceledException`) while keeping the server alive and ready for the next run.

### 3. Running & Re-Initializing in the Client GUI (`commivoy_app`)
1. Launch `commivoy_app` (preferably built in `Release` configuration).
2. Add your worker URLs (e.g., `http://localhost:5001`, `http://localhost:5002`) to the services list.
3. **Important — Re-initializing Servers After Restart**: Every time you restart the worker console windows (or before starting a new session after stopping servers), **you must re-initialize / re-check the servers in the client application** (via the ping/initialization button). Restarting a worker wipes its in-memory `TaskId` matrix cache and resets HTTP connection states, so the client must re-verify node availability before dispatching slices.
4. Set the number of cities ($N$), population size, generations, `MasterSeed`, and thread count, then click **Start**.

> **Performance Note:** For small problem sizes ($N < 20$ cities), HTTP serialization and network round-trip latency outweigh the actual genetic computation time. To observe real distributed speedup, test with $N \ge 100$ cities and population sizes $\ge 200$.

---

## Testing (`commivoy_service.Tests`)

Run the full test suite in Visual Studio via **Test** -> **Run All Tests** (`Ctrl+R, A`) or via CLI:
```cmd
dotnet test
```

### Covered Scenarios
* **Unit & Math Tests (`AlgorithmTests`, `AdvancedAlgorithmTests`)**:
  * Exact tour length calculation and valid permutation checks (no duplicate or missing cities).
  * `FastRandom` statistical uniformity across $100\,000$ iterations.
  * `2-Opt` local search verification (uncrossing intersecting diagonal routes on a square).
  * Single-threaded vs. 12-threaded execution returning bit-exact identical routes.
* **API Integration Tests (`GaControllerTests` via `WebApplicationFactory`)**:
  * `Ping_ReturnsConnectedStatus`: Verifies `/api/Ga/ping` returns `200 OK`.
  * `EvolveSlice_SplitAcrossTwoNodes_MatchesSingleSolverBitExact`: Splits a full multi-generation run across two simulated nodes and asserts bit-exact equivalence with local `GeneticSolver`.
  * `EvolveSlice_MissingCache_ReturnsConflict`: Ensures `409 Conflict` is returned when `Distances` is `null` for an unknown `TaskId`.
  * `EvolveSlice_ConcurrentRequests_HandleCacheSafely`: Fires 10 parallel requests simultaneously to verify thread-safe matrix caching.
  * `EvolveSlice_ClientCancellation_DoesNotCrashServer`: Simulates abrupt client cancellation and verifies the server stays healthy on subsequent pings.
  * `EvolveSlice_EmptyOrReversedRange_ReturnsEmptyOffspring`: Validates safe handling of boundary slice indices (`StartIndex >= EndIndex`).
  * `EvolveSlice_DimensionMismatch_ReturnsBadRequest`: Ensures `400 Bad Request` when route lengths do not match the distance matrix size.

---
---

## Русская версия

# Распределённое приложение для решения задачи коммивояжёра (TSP)

Данный проект решает задачу коммивояжёра (TSP) с помощью высокопроизводительного детерминированного генетического алгоритма (ГА), распределённого между независимыми веб-сервисами на базе ASP.NET Core. В отличие от классической «островной» модели, здесь реализован **параллелизм по данным (нарезка поколений на срезы)**, что гарантирует **побитовую детерминированность (bit-exact)** — распределённый кластер находит ровно тот же маршрут, что и локальный однопоточный алгоритм.

## Структура решения

Решение состоит из 4 проектов:

1. **commivoy** — общая библиотека классов (.NET Class Library).
   * `GeneticSolver`: ядро генетического алгоритма с плоскими одномерными массивами расстояний (`double[]`), турнирной селекцией, упорядоченным кроссовером (`OX1`), мутацией обменом, локальной оптимизацией `2-Opt` и многопоточностью через `Parallel.For` и `Partitioner`.
   * `FastRandom`: быстрый стековый генератор псевдослучайных чисел без блокировок, привязанный к `(MasterSeed, IndividualIndex, Generation)`. Исключает гонку потоков и гарантирует идентичный результат при любом количестве потоков и серверов.
   * `MatrixGenerator`: детерминированный генератор координат городов и матрицы евклидовых расстояний по заданному сиду.
   * `SliceTaskRequest` / `SliceTaskResult`: модели передачи данных (DTO) для расчёта срезов популяции по сети.

2. **commivoy_service** — вычислительный Web API сервис-воркер (ASP.NET Core).
   * Предоставляет эндпоинты проверки связи и эволюции среза популяции (`/api/Ga/ping` и `/api/Ga/evolve-slice`).
   * Кэширует матрицы расстояний в оперативной памяти по `TaskId`, чтобы не передавать тяжёлую матрицу по сети на каждом поколении.
   * Корректно обрабатывает обрыв соединения со стороны клиента (`OperationCanceledException`) без аварийного закрытия процесса сервера.

3. **commivoy_app** — клиентское настольное приложение (WPF).
   * Выступает в роли координатора (Master): опрашивает серверы, делит популяцию каждого поколения на срезы и рассылает их активным узлам.
   * Выполняет сетевую координацию в фоновом потоке, чтобы интерфейс не зависал во время расчётов.
   * Отображает карту городов, найденный оптимальный маршрут и график сходимости в реальном времени.

4. **commivoy_service.Tests** — проект модульных и интеграционных тестов (xUnit).
   * `AlgorithmTests` и `AdvancedAlgorithmTests`: модульные тесты математики (расчёт длины пути, валидность перестановок, равномерность `FastRandom`, распутывание пересечений в `2-Opt`, работа `CancellationToken` и побитовое совпадение 1 и 12 потоков).
   * `GaControllerTests`: интеграционные тесты Web API на базе `WebApplicationFactory<Program>` (`Microsoft.AspNetCore.Mvc.Testing`), поднимающие сервер прямо в памяти без занятия реальных сетевых портов.

---

## Архитектура и ключевые особенности

### 1. Нарезка поколений (Data Parallelism) и детерминизм
Вместо изолированной эволюции на разных узлах клиент синхронно координирует общую популяцию на каждом поколении:
* Лучшие особи (элита) переносятся в следующее поколение локально.
* Остальной диапазон индексов `[EliteCount .. PopulationSize)` делится на части (срезы) и параллельно отправляется по HTTP POST на активные серверы.
* Так как `FastRandom` вычисляет случайные числа строго от `(MasterSeed, IndividualIndex, Generation)`, расчёт на 1, 2 или 10 серверах даёт **абсолютно идентичный маршрут и длину**, как при локальном запуске в один поток.

### 2. Кэширование матриц и отказоустойчивость (`409 Conflict`)
* На `0`-м поколении клиент отправляет полную матрицу расстояний вместе с уникальным идентификатором задачи `TaskId`. Сервер сохраняет её в кэш в оперативной памяти.
* На всех последующих поколениях (`Generation > 0`) клиент передаёт `Distances = null`, многократно снижая объём JSON-трафика.
* Если один из серверов был перезапущен или подключился в середине расчёта и не имеет матрицы в кэше, он возвращает статус **`409 Conflict`**. Клиент автоматически перехватывает этот ответ и повторяет запрос уже с приложенной матрицей.

### 3. Оптимизация производительности (режим `Release`)
* **Плоские массивы**: двумерные матрицы разворачиваются в непрерывный массив `double[]` для максимальной скорости работы кэша процессора в циклах `2-Opt`.
* **Отключение лишнего логирования**: запуск в конфигурации `Release` отключает подробный вывод каждого HTTP-запроса в консоль, благодаря чему процессор не тратит время на медленный ввод-вывод в окно терминала.

---

## Описание API (`commivoy_service`)

### 1. Проверка доступности узла (Ping)
`GET /api/Ga/ping`

Возвращает `200 OK` со строкой статуса, подтверждающей готовность узла к работе.

### 2. Эволюция среза популяции
`POST /api/Ga/evolve-slice`

**Тело запроса (`SliceTaskRequest`):**
```json
{
  "taskId": "a1b2c3d4e5f6...",
  "distances": [
    [0.0, 10.5, 15.2],
    [10.5, 0.0, 20.1],
    [15.2, 20.1, 0.0]
  ],
  "population": [
    [0, 1, 2],
    [2, 1, 0]
  ],
  "fitness": [45.8, 45.8],
  "masterSeed": 42,
  "generation": 0,
  "startIndex": 2,
  "endIndex": 10,
  "mutationProb": 0.2,
  "threads": 4
}
```
*(Примечание: для поколений $> 0$ поле `distances` передаётся как `null`, так как матрица уже закэширована на сервере.)*

**Ответ (`SliceTaskResult`):**
```json
{
  "offspring": [
    [0, 2, 1],
    [1, 0, 2]
  ],
  "offspringFitness": [45.8, 45.8]
}
```

**Коды ответа HTTP:**
* `200 OK`: срез успешно обработан (если `startIndex >= endIndex`, возвращаются пустые массивы).
* `400 Bad Request`: некорректные данные или несовпадение размерности матрицы и длины маршрутов.
* `409 Conflict`: передан `distances: null`, но `taskId` отсутствует в кэше сервера (сигнал клиенту переотправить матрицу).

---

## Инструкция по запуску и настройке

### 1. Запуск серверов через командную строку (CMD)
Для максимальной скорости вычислений серверы необходимо запускать строго в конфигурации **`Release`**:

1. Откройте папку проекта **`commivoy_service`** в Проводнике Windows.
2. Нажмите на адресную строку папки, введите `cmd` и нажмите **Enter** — командная строка откроется сразу в нужной директории.
3. Введите команду запуска сервера на нужном порту:
   ```cmd
   dotnet run -c Release --urls "http://localhost:5001"
   ```
4. Чтобы поднять несколько узлов кластера на одном ПК, откройте ещё одно окно `cmd` из папки `commivoy_service` и запустите второй экземпляр на другом порту:
   ```cmd
   dotnet run -c Release --urls "http://localhost:5002"
   ```

### 2. Что отображается в консольном окне во время работы
При запуске и работе сервера в окне `cmd` можно наблюдать следующие этапы:
* **При запуске**: стандартные сообщения веб-сервера Kestrel о прослушиваемом адресе (`Now listening on: http://localhost:5001`) и готовности к работе (`Application started. Press Ctrl+C to shut down.`).
* **В процессе расчёта**: в режиме `Release` спам логами каждого HTTP-запроса отключён ради максимальной производительности, поэтому окно консоли остаётся практически чистым, пока ядра процессора нагружены вычислениями `Parallel.For`. При старте новой задачи могут появляться краткие сервисные сообщения о кэшировании матрицы.
* **При нажатии «Остановить» в клиенте**: если прервать расчёт в середине работы, клиент разорвёт HTTP-соединение. В консоли сервера может появиться перехваченное уведомление об отмене запроса (`OperationCanceledException` / отмена клиентом), после чего сервер останется запущенным и готовым к новым задачам.

### 3. Запуск и повторная инициализация в GUI-клиенте (`commivoy_app`)
1. Запустите клиентское приложение `commivoy_app` (также рекомендуется в режиме `Release`).
2. Добавьте адреса запущенных серверов (например, `http://localhost:5001`, `http://localhost:5002`) в список узлов.
3. **Важно — повторная инициализация серверов после перезапуска**: каждый раз после перезапуска окон серверов (или перед новым запуском после их остановки) **необходимо заново инициализировать / опросить серверы в клиентском приложении** (нажать кнопку проверки/инициализации узлов). При перезапуске процесса `commivoy_service` его оперативная память (кэш матриц `TaskId`) и сетевые сессии сбрасываются, поэтому клиент должен заново установить связь и подтвердить готовность узлов перед стартом расчёта.
4. Задайте количество городов ($N$), размер популяции, число поколений, `MasterSeed` и количество потоков, затем нажмите **Запустить**.

> **Примечание по производительности:** На малом числе городов ($N < 20$) накладные расходы на сериализацию JSON и HTTP-запросы превышают время самих вычислений. Чтобы увидеть реальный выигрыш от распределённых вычислений, запускайте задачи с $N \ge 100$ городов и размером популяции $\ge 200$.

---

## Тестирование (`commivoy_service.Tests`)

Запуск всех тестов в Visual Studio: **Тест** -> **Запустить все тесты** (`Ctrl+R, A`) или через терминал:
```cmd
dotnet test
```

### Покрываемые сценарии
* **Модульные тесты ядра (`AlgorithmTests`, `AdvancedAlgorithmTests`)**:
  * Проверка расчёта длины маршрута и целостности перестановок (отсутствие дубликатов и пропущенных городов).
  * Статистическая равномерность генератора `FastRandom` на выборке из $100\,000$ итераций.
  * Тест локального поиска `2-Opt` (распутывание пересекающихся диагональных маршрутов).
  * Сравнение 1 потока и 12 потоков — подтверждение побитовой идентичности результатов.
* **Интеграционные тесты API (`GaControllerTests` на базе `WebApplicationFactory`)**:
  * `Ping_ReturnsConnectedStatus`: проверка эндпоинта `/api/Ga/ping` (статус `200 OK`).
  * `EvolveSlice_SplitAcrossTwoNodes_MatchesSingleSolverBitExact`: сквозной тест деления 15 поколений на 2 виртуальных узла с проверкой побитового совпадения с эталонным локальным солвером.
  * `EvolveSlice_MissingCache_ReturnsConflict`: проверка возврата `409 Conflict` при отправке `Distances = null` для неизвестного `TaskId`.
  * `EvolveSlice_ConcurrentRequests_HandleCacheSafely`: одновременная отправка 10 параллельных запросов для проверки потокобезопасности кэша матриц.
  * `EvolveSlice_ClientCancellation_DoesNotCrashServer`: имитация резкой отмены запроса клиентом и проверка того, что сервер продолжает стабильно отвечать на последующие запросы.
  * `EvolveSlice_EmptyOrReversedRange_ReturnsEmptyOffspring`: проверка граничных индексов среза (`StartIndex >= EndIndex`).
  * `EvolveSlice_DimensionMismatch_ReturnsBadRequest`: проверка возврата `400 Bad Request` при несовпадении размера матрицы и длины маршрутов.