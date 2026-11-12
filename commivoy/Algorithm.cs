using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace commivoy
{
    public class GenerationInfo
    {
        public int Generation { get; set; }
        public double BestLength { get; set; }
        public double AvgLength { get; set; }
        public int[] BestOrder { get; set; }
    }

    public class MatrixGenerator
    {
        public double[,] GenerateCoords(int N, int? seed = null)
        {
            Random rnd = seed.HasValue ? new Random(seed.Value) : new Random();
            double[,] coords = new double[N, 2];
            for (int i = 0; i < N; i++)
            {
                coords[i, 0] = rnd.NextDouble() * 1000;
                coords[i, 1] = rnd.NextDouble() * 1000;
            }
            return coords;
        }

        public double[,] GenerateMatrix(double[,] coords, int N)
        {
            double[,] D = new double[N, N];
            for (int i = 0; i < N; i++)
                for (int j = 0; j < N; j++)
                {
                    if (i == j) D[i, j] = 0;
                    else
                    {
                        double dx = coords[i, 0] - coords[j, 0];
                        double dy = coords[i, 1] - coords[j, 1];
                        D[i, j] = Math.Sqrt(dx * dx + dy * dy);
                    }
                }
            return D;
        }
    }

    // Запрос на обработку одного среза [StartIndex, EndIndex) в рамках поколения Generation
    public class SliceTaskRequest
    {
        public string TaskId { get; set; }
        // Передаётся только в 0-м поколении (или при ретрае), далее сервер берёт из кэша по TaskId
        public double[][]? Distances { get; set; }
        public int[][] Population { get; set; }
        public double[] Fitness { get; set; }
        public int MasterSeed { get; set; }
        public int Generation { get; set; }
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public double MutationProb { get; set; }
        public int Threads { get; set; }
    }

    // Ответ сервера с посчитанным куском нового поколения
    public class SliceTaskResult
    {
        public int StartIndex { get; set; }
        public int EndIndex { get; set; }
        public int[][] Offspring { get; set; }
        public double[] OffspringFitness { get; set; }
        public double ExecutionTimeMs { get; set; }
    }

    public class GeneticSolver
    {
        private readonly double[,] _d;
        private readonly int _n;
        private readonly int _seedBase;
        private readonly bool _fixedSeed;
        private readonly Random _initRnd;

        public int PopulationSize { get; set; } = 100;
        public double MutationProb { get; set; } = 0.2;
        public int MaxGenerations { get; set; } = 200;
        public int EliteCount { get; set; } = 2;

        private int _threads = Environment.ProcessorCount;
        public int Threads
        {
            get => _threads;
            set => _threads = Math.Max(1, Math.Min(value, Environment.ProcessorCount));
        }

        public event Action<GenerationInfo> OnGeneration;
        public event Action<int[]> OnNewBest;

        public GeneticSolver(double[,] distances, int? seed = null)
        {
            _d = distances;
            _n = distances.GetLength(0);
            _fixedSeed = seed.HasValue;
            _seedBase = seed ?? Environment.TickCount;
            _initRnd = new Random(_seedBase);
        }

        // Локальный запуск (используется в кнопке "Замер ускорения" и как эталон)
        public (int[] order, double length) Solve(CancellationToken ct)
        {
            if (_n == 0) return (Array.Empty<int>(), 0);
            if (_n == 1) return (new[] { 0 }, 0);

            var pop = CreateInitialPopulation(_n, PopulationSize, _seedBase);
            var fit = new double[PopulationSize];
            for (int i = 0; i < PopulationSize; i++)
                fit[i] = PathLength(pop[i], _d);

            int[] bestOrder = null;
            double bestLen = double.MaxValue;

            for (int gen = 0; gen < MaxGenerations && !ct.IsCancellationRequested; gen++)
            {
                int bi = 0;
                for (int i = 1; i < fit.Length; i++)
                    if (fit[i] < fit[bi]) bi = i;

                if (fit[bi] < bestLen)
                {
                    bestLen = fit[bi];
                    bestOrder = (int[])pop[bi].Clone();
                    OnNewBest?.Invoke((int[])bestOrder.Clone());
                }

                OnGeneration?.Invoke(new GenerationInfo
                {
                    Generation = gen,
                    BestLength = bestLen,
                    AvgLength = fit.Average(),
                    BestOrder = (int[])bestOrder.Clone()
                });

                var sortedIdx = Enumerable.Range(0, PopulationSize)
                                          .OrderBy(i => fit[i])
                                          .ToList();
                int elites = Math.Min(EliteCount, sortedIdx.Count);

                var nextPop = new int[PopulationSize][];
                var nextFit = new double[PopulationSize];
                for (int k = 0; k < elites; k++)
                {
                    nextPop[k] = (int[])pop[sortedIdx[k]].Clone();
                    nextFit[k] = fit[sortedIdx[k]];
                }

                try
                {
                    var (slicePop, sliceFit) = EvolveSlice(
                        _d, pop, fit, _seedBase, _fixedSeed, gen,
                        elites, PopulationSize, MutationProb, Threads, ct);

                    for (int idx = 0; idx < slicePop.Length; idx++)
                    {
                        nextPop[elites + idx] = slicePop[idx];
                        nextFit[elites + idx] = sliceFit[idx];
                    }
                }
                catch (OperationCanceledException) { break; }

                pop = nextPop;
                fit = nextFit;
            }

            return (bestOrder, bestLen);
        }

        // Детерминированная генерация начальной популяции от одного MasterSeed
        public static int[][] CreateInitialPopulation(int cityCount, int popSize, int masterSeed)
        {
            var rnd = new Random(masterSeed);
            var pop = new int[popSize][];
            for (int i = 0; i < popSize; i++)
            {
                var p = Enumerable.Range(0, cityCount).ToArray();
                Shuffle(p, rnd);
                pop[i] = p;
            }
            return pop;
        }

        // Обработка одного диапазона особей [startIndex, endIndex) — вызывается и локально, и на серверах
        public static (int[][] offspring, double[] offspringFitness) EvolveSlice(
            double[,] d,
            int[][] pop,
            double[] fit,
            int masterSeed,
            bool fixedSeed,
            int gen,
            int startIndex,
            int endIndex,
            double mutationProb,
            int threads,
            CancellationToken ct)
        {
            int count = Math.Max(0, endIndex - startIndex);
            var resultPop = new int[count][];
            var resultFit = new double[count];
            int n = d.GetLength(0);

            Parallel.For(startIndex, endIndex,
                new ParallelOptions
                {
                    CancellationToken = ct,
                    MaxDegreeOfParallelism = Math.Max(1, threads)
                },
                i =>
                {
                    int s = fixedSeed
                        ? unchecked(masterSeed + i * 7919 + gen * 104729)
                        : unchecked(Environment.TickCount + i * 7919 + gen * 131);
                    var rnd = new Random(s);

                    var parent = Tournament(pop, fit, rnd);
                    var child = (int[])parent.Clone();

                    if (rnd.NextDouble() < mutationProb)
                        Mutate(child, rnd);

                    TwoOpt(child, d, n, ct);

                    int localIdx = i - startIndex;
                    resultPop[localIdx] = child;
                    resultFit[localIdx] = PathLength(child, d);
                });

            return (resultPop, resultFit);
        }

        public static double PathLength(int[] order, double[,] d)
        {
            int n = order.Length;
            double s = 0;
            for (int i = 0; i < n - 1; i++)
                s += d[order[i], order[i + 1]];
            s += d[order[n - 1], order[0]];
            return s;
        }

        private static void Shuffle(int[] a, Random rnd)
        {
            for (int i = a.Length - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                (a[i], a[j]) = (a[j], a[i]);
            }
        }

        private static int[] Tournament(int[][] pop, double[] fit, Random rnd)
        {
            int a = rnd.Next(pop.Length);
            int b = rnd.Next(pop.Length);
            return fit[a] <= fit[b] ? pop[a] : pop[b];
        }

        private static void Mutate(int[] order, Random rnd)
        {
            int i = rnd.Next(order.Length);
            int j = rnd.Next(order.Length);
            (order[i], order[j]) = (order[j], order[i]);
        }

        private static void TwoOpt(int[] route, double[,] d, int n, CancellationToken ct)
        {
            bool improved = true;
            while (improved)
            {
                if (ct.IsCancellationRequested) return;
                improved = false;

                for (int i = 0; i < n - 1; i++)
                {
                    for (int j = i + 2; j < n; j++)
                    {
                        if (i == 0 && j == n - 1) continue;
                        int a = route[i], b = route[i + 1];
                        int c = route[j], e = route[(j + 1) % n];

                        if (d[a, c] + d[b, e] < d[a, b] + d[c, e])
                        {
                            Array.Reverse(route, i + 1, j - i);
                            improved = true;
                        }
                    }
                }
            }
        }
    }
}