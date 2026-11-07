using System.Net;
using System.Net.Http.Json;
using commivoy;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace commivoy_service.Tests
{
    public class GaControllerTests : IClassFixture<WebApplicationFactory<Program>>
    {
        private readonly HttpClient _client;

        public GaControllerTests(WebApplicationFactory<Program> factory)
        {
            // Создаём in-memory HTTP-клиент (согласно шаблону Тим Дешривера)
            _client = factory.CreateClient();
        }

        [Fact]
        public async Task Solve_ValidRequest_ReturnsOkWithResult()
        {
            // Arrange
            var request = new DistributedTaskRequest
            {
                Distances = new double[][]
                {
                    new double[] { 0, 10, 15 },
                    new double[] { 10, 0, 20 },
                    new double[] { 15, 20, 0 }
                },
                Seed = 42,
                PopulationSize = 20,
                MaxGenerations = 50,
                MutationProb = 0.2,
                Threads = 1
            };

            // Act
            var response = await _client.PostAsJsonAsync("/api/Ga/solve", request);

            // Assert
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var result = await response.Content.ReadFromJsonAsync<DistributedTaskResult>();
            Assert.NotNull(result);
            Assert.NotNull(result.BestOrder);
            Assert.Equal(3, result.BestOrder.Length);
            Assert.True(result.BestLength > 0);
        }

        [Fact]
        public async Task Solve_SameSeed_ReturnsDeterministicResult()
        {
            // Arrange
            var request = new DistributedTaskRequest
            {
                Distances = new double[][]
                {
                    new double[] { 0, 5, 10 },
                    new double[] { 5, 0, 8 },
                    new double[] { 10, 8, 0 }
                },
                Seed = 100,
                PopulationSize = 30,
                MaxGenerations = 100,
                MutationProb = 0.1,
                Threads = 1
            };

            // Act
            var response1 = await _client.PostAsJsonAsync("/api/Ga/solve", request);
            var response2 = await _client.PostAsJsonAsync("/api/Ga/solve", request);

            var result1 = await response1.Content.ReadFromJsonAsync<DistributedTaskResult>();
            var result2 = await response2.Content.ReadFromJsonAsync<DistributedTaskResult>();

            // Assert
            Assert.Equal(result1.BestLength, result2.BestLength, precision: 4);
            Assert.Equal(result1.BestOrder, result2.BestOrder);
        }

        [Fact]
        public async Task Solve_InvalidMatrix_ReturnsBadRequest()
        {
            // Arrange
            var request = new DistributedTaskRequest
            {
                Distances = null,
                Seed = 42,
                PopulationSize = 10,
                MaxGenerations = 10,
                MutationProb = 0.2,
                Threads = 1
            };

            // Act
            var response = await _client.PostAsJsonAsync("/api/Ga/solve", request);

            // Assert
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }
}