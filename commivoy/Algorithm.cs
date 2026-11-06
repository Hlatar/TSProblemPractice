using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace commivoy
{
    public class Algorithm { }

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

    public class GeneticSolver
    {
        private readonly double[,] _d;
        private readonly int _n;

        // базовый сид для воспроизводимости; null = случайный
        private readonly int _seedBase;
        private readonly bool _fixedSeed;

        // Random только для однопоточных участков (инициализация, Shuffle)
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

        // seed = null  → каждый запуск случайный
        // seed = число → воспроизводимо от запуска к запуску
        public GeneticSolver(double[,] distances, int? seed = null)
        {
            _d = distances;
            _n = distances.GetLength(0);

            _fixedSeed = seed.HasValue;
            _seedBase = seed ?? Environment.TickCount;
            _initRnd = new Random(_seedBase);
        }

        // ---------- Основной метод ----------
        public (int[] order, double length) Solve(CancellationToken ct)
        {
            if (_n == 0) return (Array.Empty<int>(), 0);
            if (_n == 1) return (new[] { 0 }, 0);

            // Начальная популяция (однопоточно, детерминирована при fixed seed)
            var pop = new List<int[]>(PopulationSize);
            for (int i = 0; i < PopulationSize; i++)
            {
                var p = Enumerable.Range(0, _n).ToArray();
                Shuffle(p, _initRnd);
                pop.Add(p);
            }

            int[] bestOrder = null;
            double bestLen = double.MaxValue;

            //  Поколения
            for (int gen = 0; gen < MaxGenerations && !ct.IsCancellationRequested; gen++)
            {
                //  Оценка приспособленности 
                var fit = new double[pop.Count];
                try
                {
                    Parallel.For(0, pop.Count,
                        new ParallelOptions
                        {
                            CancellationToken = ct,
                            MaxDegreeOfParallelism = Threads
                        },
                        i => fit[i] = PathLength(pop[i]));
                }
                catch (OperationCanceledException) { break; }

                // Лучший в поколении
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

                // Формируем новое поколение

                // элита — клонируется последовательно
                var sortedIdx = Enumerable.Range(0, pop.Count)
                                          .OrderBy(i => fit[i])
                                          .ToList();
                int elites = Math.Min(EliteCount, sortedIdx.Count);

                var nextPop = new int[PopulationSize][];
                for (int k = 0; k < elites; k++)
                    nextPop[k] = (int[])pop[sortedIdx[k]].Clone();

                // остальные — параллельно
                try
                {
                    Parallel.For(elites, PopulationSize,
                        new ParallelOptions
                        {
                            CancellationToken = ct,
                            MaxDegreeOfParallelism = Threads
                        },
                        i =>
                        {
                            // свой Random на каждую итерацию.
                            // Если задан фиксированный seed — тоже детерминировано.
                            int s = _fixedSeed
                                ? unchecked(_seedBase + i * 7919)
                                : unchecked(Environment.TickCount + i * 7919 + gen * 131);
                            var rnd = new Random(s);

                            var parent = Tournament(pop, fit, rnd);
                            var child = (int[])parent.Clone();

                            if (rnd.NextDouble() < MutationProb)
                                Mutate(child, rnd);

                            TwoOpt(child, ct);

                            nextPop[i] = child;
                        });
                }
                catch (OperationCanceledException) { break; }

                pop = nextPop.ToList();
            }

            return (bestOrder, bestLen);
        }

        // ---------- Вспомогательные ----------

        private double PathLength(int[] order)
        {
            double s = 0;
            for (int i = 0; i < _n - 1; i++)
                s += _d[order[i], order[i + 1]];
            s += _d[order[_n - 1], order[0]];
            return s;
        }

        private void Shuffle(int[] a, Random rnd)
        {
            for (int i = a.Length - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                (a[i], a[j]) = (a[j], a[i]);
            }
        }

        private int[] Tournament(List<int[]> pop, double[] fit, Random rnd)
        {
            int a = rnd.Next(pop.Count);
            int b = rnd.Next(pop.Count);
            return fit[a] <= fit[b] ? pop[a] : pop[b];
        }

        private void Mutate(int[] order, Random rnd)
        {
            int i = rnd.Next(order.Length);
            int j = rnd.Next(order.Length);
            (order[i], order[j]) = (order[j], order[i]);
        }

        // 2-opt
        private void TwoOpt(int[] route, CancellationToken ct)
        {
            bool improved = true;
            while (improved)
            {
                if (ct.IsCancellationRequested) return;
                improved = false;

                for (int i = 0; i < _n - 1; i++)
                {
                    for (int j = i + 2; j < _n; j++)
                    {
                        if (i == 0 && j == _n - 1) continue;
                        int a = route[i], b = route[i + 1];
                        int c = route[j], d = route[(j + 1) % _n];

                        if (_d[a, c] + _d[b, d] < _d[a, b] + _d[c, d])
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