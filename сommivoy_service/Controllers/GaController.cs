using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using commivoy;

namespace commivoy_service.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class GaController : ControllerBase
    {
        private readonly ILogger<GaController> _logger;

        // Кэш матриц расстояний по TaskId, чтобы не пересылать матрицу на каждом поколении
        private static readonly ConcurrentDictionary<string, double[,]> _distanceCache = new();

        public GaController(ILogger<GaController> logger)
        {
            _logger = logger;
        }

        // Проверка живого коннекта
        [HttpGet("ping")]
        public IActionResult Ping()
        {
            string clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "localhost";
            _logger.LogInformation("[PING] Подключен клиент {ClientIp}", clientIp);
            return Ok(new { status = "Connected", timestamp = DateTime.UtcNow });
        }

        // Вычисление назначенного диапазона особей [StartIndex, EndIndex) для поколения Generation
        [HttpPost("evolve-slice")]
        public IActionResult EvolveSlice([FromBody] SliceTaskRequest request, CancellationToken cancellationToken)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.TaskId) || request.Population == null)
            {
                return BadRequest("Некорректные параметры запроса.");
            }

            try
            {
                double[,] matrix;
                if (request.Distances != null)
                {
                    int n = request.Distances.Length;
                    matrix = new double[n, n];
                    for (int i = 0; i < n; i++)
                        for (int j = 0; j < n; j++)
                            matrix[i, j] = request.Distances[i][j];

                    _distanceCache[request.TaskId] = matrix;
                    _logger.LogInformation("[INIT] Задача {TaskId} инициализирована (N={Cities}, MasterSeed={Seed})",
                        request.TaskId, n, request.MasterSeed);
                }
                else if (!_distanceCache.TryGetValue(request.TaskId, out matrix))
                {
                    // Если сервер перезагрузился в середине расчёта, просим клиента прислать матрицу повторно
                    _logger.LogWarning("[CACHE MISS] Матрица для задачи {TaskId} не найдена в кэше.", request.TaskId);
                    return Conflict("CACHE_MISS");
                }

                var sw = Stopwatch.StartNew();

                var (offspring, offspringFit) = GeneticSolver.EvolveSlice(
                    matrix,
                    request.Population,
                    request.Fitness,
                    request.MasterSeed,
                    fixedSeed: true,
                    request.Generation,
                    request.StartIndex,
                    request.EndIndex,
                    request.MutationProb,
                    request.Threads,
                    cancellationToken);

                sw.Stop();

                if (request.Generation % 25 == 0)
                {
                    _logger.LogInformation("[SLICE] Task={TaskId} | Gen={Gen} | Slice=[{Start}..{End}) | {Ms} мс",
                        request.TaskId, request.Generation, request.StartIndex, request.EndIndex, sw.ElapsedMilliseconds);
                }

                return Ok(new SliceTaskResult
                {
                    StartIndex = request.StartIndex,
                    EndIndex = request.EndIndex,
                    Offspring = offspring,
                    OffspringFitness = offspringFit,
                    ExecutionTimeMs = sw.ElapsedMilliseconds
                });
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("[CANCELLED] Задача {TaskId} отменена на поколении {Gen}.", request.TaskId, request.Generation);
                return StatusCode(499, "Вычисление отменено клиентом");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ERROR] Сбой при обработке среза [{Start}..{End}) задачи {TaskId}",
                    request.StartIndex, request.EndIndex, request.TaskId);
                return BadRequest($"Ошибка сервера: {ex.Message}");
            }
        }
    }
}