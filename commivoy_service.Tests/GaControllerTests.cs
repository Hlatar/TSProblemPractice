using commivoy;
using System;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Microsoft.AspNetCore.Mvc.Testing;

namespace commivoy_service.Tests
{
    public partial class GaControllerTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly HttpClient _client;

        public GaControllerTests(WebApplicationFactory<Program> factory)
        {
            _client = factory.CreateClient();
        }

        [Fact]
        public async Task EvolveSlice_MissingCache_ReturnsConflict()
        {
            var req = new SliceTaskRequest
            {
                TaskId = Guid.NewGuid().ToString("N"),
                Distances = null,
                Population = new[] { new[] { 0, 1 }, new[] { 1, 0 } },
                Fitness = new[] { 10.0, 10.0 },
                StartIndex = 0,
                EndIndex = 2
            };

            var response = await _client.PostAsJsonAsync("/api/Ga/evolve-slice", req);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        [Fact]
        public async Task EvolveSlice_ConcurrentRequests_HandleCacheSafely()
        {
            string taskId = Guid.NewGuid().ToString("N");
            double[][] dist = { new[] { 0.0, 10.0 }, new[] { 10.0, 0.0 } };
            int[][] pop = { new[] { 0, 1 }, new[] { 1, 0 } };
            double[] fit = { 10.0, 10.0 };

            var req = new SliceTaskRequest
            {
                TaskId = taskId,
                Distances = dist,
                Population = pop,
                Fitness = fit,
                StartIndex = 0,
                EndIndex = 2,
                Threads = 1
            };

            var tasks = Enumerable.Range(0, 10)
                .Select(_ => _client.PostAsJsonAsync("/api/Ga/evolve-slice", req));

            var responses = await Task.WhenAll(tasks);

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        }

        [Fact]
        public async Task EvolveSlice_ClientCancellation_DoesNotCrashServer()
        {
            var req = new SliceTaskRequest
            {
                TaskId = Guid.NewGuid().ToString("N"),
                Distances = new[] { new[] { 0.0, 10.0 }, new[] { 10.0, 0.0 } },
                Population = new[] { new[] { 0, 1 }, new[] { 1, 0 } },
                Fitness = new[] { 10.0, 10.0 },
                StartIndex = 0,
                EndIndex = 2,
                Threads = 1
            };

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                _client.PostAsJsonAsync("/api/Ga/evolve-slice", req, cts.Token));

            var ping = await _client.GetAsync("/api/Ga/ping");
            Assert.Equal(HttpStatusCode.OK, ping.StatusCode);
        }

        [Theory]
        [InlineData(5, 5)]
        [InlineData(10, 5)]
        public async Task EvolveSlice_EmptyOrReversedRange_ReturnsEmptyOffspring(int start, int end)
        {
            var req = new SliceTaskRequest
            {
                TaskId = Guid.NewGuid().ToString("N"),
                Distances = new[] { new[] { 0.0, 10.0 }, new[] { 10.0, 0.0 } },
                Population = new[] { new[] { 0, 1 }, new[] { 1, 0 }, new[] { 0, 1 } },
                Fitness = new[] { 10.0, 10.0, 10.0 },
                StartIndex = start,
                EndIndex = end
            };

            var response = await _client.PostAsJsonAsync("/api/Ga/evolve-slice", req);
            var result = await response.Content.ReadFromJsonAsync<SliceTaskResult>();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(result);
            Assert.Empty(result.Offspring);
        }

        [Fact]
        public async Task EvolveSlice_DimensionMismatch_ReturnsBadRequest()
        {
            var req = new SliceTaskRequest
            {
                TaskId = Guid.NewGuid().ToString("N"),
                Distances = new[] { new[] { 0.0, 10.0 }, new[] { 10.0, 0.0 } },
                Population = new[] { new[] { 0, 1, 2 }, new[] { 2, 1, 0 } },
                Fitness = new[] { 10.0, 10.0 },
                StartIndex = 0,
                EndIndex = 2
            };

            var response = await _client.PostAsJsonAsync("/api/Ga/evolve-slice", req);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

    }
}