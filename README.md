# Distributed TSP Solver (Island Model GA)

[English](#english-version) | [Русский](#русская-версия)

## English Version

# Distributed Traveling Salesperson Problem (TSP) Solver

This project solves the Traveling Salesperson Problem (TSP) using a parallel Genetic Algorithm (GA) distributed across independent ASP.NET Core REST API microservices using the **Island Model**.

## Solution Structure

The solution consists of 4 projects:

1. **commivoy** — Core Shared Class Library (.NET).

   * `GeneticSolver`: Implements the genetic algorithm core featuring tournament selection, order mutation, 2-Opt local search, and multi-core evaluation via `Parallel.For`.

   * `MatrixGenerator`: Generates random city coordinates and corresponding distance matrices.

   * `DistributedTaskRequest` / `DistributedTaskResult`: Data Transfer Objects (DTOs) for network communication.

2. **commivoy_service** — Computational ASP.NET Core Web API Service.

   * Receives sub-population configurations and distance matrices via HTTP POST.

   * Executes the genetic algorithm across assigned CPU threads.

   * Supports task cancellation via `CancellationToken` upon client connection drop.

3. **commivoy_app** — WPF Client Desktop Application.

   * Manages a list of remote compute service endpoints.

   * Distributes workload across nodes with distinct deterministic `Seed` values per island.

   * Visualizes city locations, calculated route topology, and convergence charts.

4. **commivoy_service.Tests** — Integration Testing Project (xUnit).

   * Automated controller testing using in-memory host execution (`WebApplicationFactory`) without binding to physical network sockets.

## System Architecture

### 1. Island Model Distribution

When initiating computation, the client WPF application:

* Sends the full distance matrix to each active compute service along with a unique `Seed` (`baseSeed + GetHashCode()`).

* Compute nodes execute genetic optimization concurrently on isolated sub-populations ("islands").

* The WPF client asynchronously awaits all HTTP responses and selects the route with the global minimum length.

### 2. Cooperative Cancellation Mechanism

Pressing the **Stop** button in the WPF UI triggers a `CancellationTokenSource`. Pending HTTP requests are aborted, which signals ASP.NET Core's `HttpContext.RequestAborted` token and terminates internal `Parallel.For` loops on the server without consuming unnecessary CPU resources.

## API Documentation (commivoy_service)

### Compute Endpoint

`POST /api/Ga/solve`

**Request Body (`DistributedTaskRequest`):**

```json
{
  "distances": [
    [0.0, 10.5, 15.2],
    [10.5, 0.0, 20.1],
    [15.2, 20.1, 0.0]
  ],
  "seed": 42,
  "populationSize": 100,
  "maxGenerations": 300,
  "mutationProb": 0.2,
  "threads": 4
}
```

**Response Body (`DistributedTaskResult`):**

```json
{
  "bestOrder": [0, 1, 2],
  "bestLength": 45.8,
  "executionTimeMs": 124,
  "serviceUrl": "http://localhost:5226"
}
```

## Integration Testing (commivoy_service.Tests)

Tests utilize `WebApplicationFactory<Program>` based on `Microsoft.AspNetCore.Mvc.Testing`.

Test Scenarios (`GaControllerTests.cs`):

* `Solve_ValidRequest_ReturnsOkWithCorrectResult`: Validates HTTP 200 OK status, JSON deserialization, and route length integrity.

* `Solve_SameSeed_ReturnsDeterministicResult`: Ensures identical results when invoking requests with identical `Seed` values.

* `Solve_InvalidMatrix_ReturnsBadRequest`: Verifies HTTP 400 Bad Request error handling when given null or invalid matrices.

Run tests in Visual Studio via **Test** -> **Run All Tests** (`Ctrl+R, A`).

## Setup and Running

### 1. Service Port Configuration

Default ports are defined in `commivoy_service/Properties/launchSettings.json` under `applicationUrl`.

To launch multiple service instances on separate local ports via CLI:

```bash
dotnet run --project commivoy_service --urls "http://localhost:5001"
dotnet run --project commivoy_service --urls "http://localhost:5002"
```

### 2. Running the Client GUI

1. Launch `commivoy_app`.

2. Add compute service endpoints (e.g., `http://localhost:5001`, `http://localhost:5226`) to the **Services** list.

3. Configure target cities, population size, generation limits, and thread count.

4. Click **Start**.

> **Performance Note:** For small datasets ($N < 20$ cities), local thread synchronization and HTTP overhead may exceed total computation time. To observe multi-core speedup, test with $N \ge 50$ cities and population sizes $\ge 500$.

---

## Русская версия

# Распределённое приложение для решения задачи коммивояжёра (TSP)

Данный проект предназначен для решения задачи коммивояжёра с использованием генетического алгоритма. Вычислительная нагрузка распределяется между независимыми веб-сервисами (REST API) на базе ASP.NET Core по «островной» модели (Island Model).

## Структура решения

Решение состоит из 4 проектов:

1. **commivoy** — общая библиотека классов (.NET Class Library).

   * `GeneticSolver`: ядро генетического алгоритма с поддержкой турнирной селекции, мутации, локальной 2-Opt оптимизации и многопоточности через `Parallel.For`.

   * `MatrixGenerator`: генератор случайных координат городов и соответствующей матрицы расстояний.

   * `DistributedTaskRequest` / `DistributedTaskResult`: классы передачи данных (DTO).

2. **commivoy_service** — вычислительный Web API сервис (ASP.NET Core).

   * Принимает параметры подпопуляции и матрицу расстояний по HTTP POST.

   * Выполняет вычисления генетического алгоритма на выделенных ядрах.

   * Поддерживает отмену вычислений через `CancellationToken` (при разрыве соединения со стороны клиента).

3. **commivoy_app** — клиентское GUI-приложение (WPF).

   * Управляет списком IP-адресов сервисов.

   * Делит вычисления между узлами сети и передает каждому из них свой детерминированный `Seed`.

   * Визуализирует карту точек, оптимальный найденный маршрут и график сходимости.

4. **commivoy_service.Tests** — проект интеграционных тестов (xUnit).

   * Выполняет автоматическое тестирование контроллера сервиса в памяти процессора без привязки к реальным сетевым портам (с использованием `WebApplicationFactory`).

## Архитектура и принципы работы

### 1. Островная модель распределения

При запуске расчета клиентский GUI распределяет задачу следующим образом:

* Каждому вычислительному узлу передается вся матрица расстояний, но генерируется уникальное значение `Seed` (`baseSeed + GetHashCode()`).

* Вычислительные узлы одновременно выполняют поиск решения в изолированных популяционных «островах».

* GUI-клиент асинхронно дожидается ответов всех узлов и выбирает маршрут с минимальной длиной.

### 2. Поддержка отмены вычислений

При нажатии кнопки «Остановить» в WPF-приложении клиент вызывает отмену `CancellationTokenSource`. Запросы HTTP прерываются, в результате чего в ASP.NET Core срабатывает токен `HttpContext.RequestAborted`, завершая циклы `Parallel.For` на сервере без холостой нагрузки на CPU.

## Описание API (commivoy_service)

### Выполнение вычислений

`POST /api/Ga/solve`

**Тело запроса (`DistributedTaskRequest`):**

```json
{
  "distances": [
    [0.0, 10.5, 15.2],
    [10.5, 0.0, 20.1],
    [15.2, 20.1, 0.0]
  ],
  "seed": 42,
  "populationSize": 100,
  "maxGenerations": 300,
  "mutationProb": 0.2,
  "threads": 4
}
```

**Ответ (`DistributedTaskResult`):**

```json
{
  "bestOrder": [0, 1, 2],
  "bestLength": 45.8,
  "executionTimeMs": 124,
  "serviceUrl": "http://localhost:5226"
}
```

## Интеграционное тестирование (commivoy_service.Tests)

Тестирование реализовано на основе паттерна `WebApplicationFactory<Program>` с использованием библиотеки `Microsoft.AspNetCore.Mvc.Testing`.

Покрываемые сценарии (`GaControllerTests.cs`):

* `Solve_ValidRequest_ReturnsOkWithCorrectResult`: проверка успешного статуса 200 OK, структуры ответа и корректности длин маршрутов.

* `Solve_SameSeed_ReturnsDeterministicResult`: проверка воспроизводимости результата при фиксированном значении `Seed`.

* `Solve_InvalidMatrix_ReturnsBadRequest`: проверка корректной обработки ошибок (возврат кода 400 Bad Request при некорректных входных данных).

Запуск тестов в Visual Studio: **Тест** -> **Запустить все тесты** (или сочетание клавиш `Ctrl+R, A`).

## Инструкция по запуску и настройке

### 1. Настройка адресов и портов сервисов

Порты по умолчанию задаются в файле `commivoy_service/Properties/launchSettings.json` в секции `applicationUrl`.

Для запуска нескольких экземпляров сервисов локально на разных портах используйте CLI:

```bash
dotnet run --project commivoy_service --urls "http://localhost:5001"
dotnet run --project commivoy_service --urls "http://localhost:5002"
```

### 2. Запуск GUI-клиента

1. Запустите проект `commivoy_app`.

2. В поле **Сервисы (IP:Port)** добавьте адреса работающих веб-сервисов (например, `http://localhost:5001` или `http://localhost:5226`).

3. Задайте число городов, размер популяции и поколений.

4. Нажмите **Запустить**.

> **Примечание по производительности:** На небольших объемах данных ($N < 20$ городов) накладные расходы на создание потоков и сетевой стек HTTP на одной физической машине могут превышать время вычислений. Для наглядной проверки ускорения от параллелизма рекомендуется устанавливать число городов $N \ge 50$ и размер популяции $\ge 500$.