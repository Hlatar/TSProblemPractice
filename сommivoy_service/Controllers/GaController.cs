using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using commivoy;

namespace commivoy_service.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class GaController : ControllerBase
    {
        [HttpPost("solve")]
        public IActionResult Solve([FromBody] DistributedTaskRequest request, CancellationToken cancellationToken)
        {
            try
            {
                int n = request.Distances.Length;
                double[,] matrix = new double[n, n];
                for (int i = 0; i < n; i++)
                {
                    for (int j = 0; j < n; j++)
                    {
                        matrix[i, j] = request.Distances[i][j];
                    }
                }

                var solver = new GeneticSolver(matrix, request.Seed)
                {
                    PopulationSize = request.PopulationSize,
                    MaxGenerations = request.MaxGenerations,
                    MutationProb = request.MutationProb,
                    Threads = request.Threads
                };

                var sw = Stopwatch.StartNew();
                // Передаём токен отмены от ASP.NET Core
                var (order, length) = solver.Solve(cancellationToken);
                sw.Stop();

                return Ok(new DistributedTaskResult
                {
                    BestOrder = order,
                    BestLength = length,
                    ExecutionTimeMs = sw.ElapsedMilliseconds
                });
            }
            catch (OperationCanceledException)
            {
                // Запрос был отменён клиентом
                return StatusCode(499, "Вычисление отменено пользователем");
            }
            catch (Exception ex)
            {
                return BadRequest($"Ошибка вычисления: {ex.Message}");
            }
        }
    }
}
