using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using commivoy;

namespace commivoy_app
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource _cts;
        private double[,] _coords;
        private readonly List<GenerationInfo> _history = new List<GenerationInfo>();
        private int[] _lastOrder;
        private int _maxGenerations = 1;

        public MainWindow()
        {
            InitializeComponent();

            int cores = Environment.ProcessorCount;
            TbThreads.Text = cores.ToString();
            TbThreads.ToolTip = $"На этом компьютере доступно логических ядер: {cores}";
            TbSeed.ToolTip = "Оставь пустым — каждый запуск будет случайным.\n" +
                             "Число — воспроизводимый результат (для отчёта).";

            CanvasMap.SizeChanged += (s, e) => RedrawAll();
            CanvasChart.SizeChanged += (s, e) => DrawChart();
        }



        // ============================================================
        //  Обычный запуск
        // ============================================================
        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadParams(out int n, out int pop, out int gens,
                               out int threads, out double pm, out int? seed))
                return;

            


            //n = 10;
            //_coords = new double[10, 2];

            //double R = 500.0;
            //double cx = 500.0, cy = 500.0;

            //for (int i = 0; i < 10; i++)
            //{
            //    double angle = 2 * Math.PI * i / 10;
            //    _coords[i, 0] = cx + R * Math.Cos(angle);
            //    _coords[i, 1] = cy + R * Math.Sin(angle);
            //}

            var gen = new MatrixGenerator();
            _coords = gen.GenerateCoords(n, seed);
            double[,] d = gen.GenerateMatrix(_coords, n);

            _maxGenerations = gens;   
            _history.Clear();
            _lastOrder = null;
            CanvasChart.Children.Clear();
            DrawCities();

            var solver = new GeneticSolver(d, seed)
            {
                PopulationSize = pop,
                MutationProb = pm,
                MaxGenerations = gens,
                Threads = threads,
                EliteCount = 2
            };

            solver.OnGeneration += info =>
                Dispatcher.Invoke(() => OnGenerationUI(info));

            solver.OnNewBest += order =>
                Dispatcher.Invoke(() =>
                {
                    _lastOrder = order;
                    DrawRoute(order);
                });

            _cts = new CancellationTokenSource();
            BtnStart.IsEnabled = false;
            BtnStop.IsEnabled = true;
            BtnBench.IsEnabled = false;
            TbStatus.Text = $"Идёт поиск... (потоков: {threads} из {Environment.ProcessorCount})";

            var token = _cts.Token;
            var sw = Stopwatch.StartNew();
            var result = await Task.Run(() => solver.Solve(token));
            sw.Stop();

            TbStatus.Text = token.IsCancellationRequested
                ? $"Остановлено за {sw.ElapsedMilliseconds} мс"
                : $"Готово за {sw.ElapsedMilliseconds} мс (потоков: {threads})";
            TbBest.Text = $"Лучшая длина: {result.length:F2}";
            TbOrder.Text = "Маршрут: " + string.Join(" → ", result.order);

            _lastOrder = result.order;
            DrawRoute(result.order);
            DrawChart();

            BtnStart.IsEnabled = true;
            BtnStop.IsEnabled = false;
            BtnBench.IsEnabled = true;
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
        }

        // ============================================================
        //  Замер ускорения от потоков
        // ============================================================
        private async void BtnBench_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadParams(out int n, out int pop, out int gens,
                               out int threads, out double pm, out int? seed))
                return;

            // Для честного замера нужен фиксированный seed
            int fixedSeed = seed ?? 42;

            var gen = new MatrixGenerator();
            _coords = gen.GenerateCoords(n, fixedSeed);
            double[,] d = gen.GenerateMatrix(_coords, n);

            TbBenchResult.Text = "Идёт замер...";
            BtnStart.IsEnabled = false;
            BtnBench.IsEnabled = false;

            var result = await Task.Run(() =>
            {
                // прогон 1: 1 поток
                var s1 = new GeneticSolver(d, fixedSeed)
                {
                    PopulationSize = pop,
                    MutationProb = pm,
                    MaxGenerations = gens,
                    Threads = 1,
                    EliteCount = 2
                };
                var sw1 = Stopwatch.StartNew();
                var r1 = s1.Solve(CancellationToken.None);
                sw1.Stop();

                // прогон 2: threads потоков, тот же seed
                var s2 = new GeneticSolver(d, fixedSeed)
                {
                    PopulationSize = pop,
                    MutationProb = pm,
                    MaxGenerations = gens,
                    Threads = threads,
                    EliteCount = 2
                };
                var sw2 = Stopwatch.StartNew();
                var r2 = s2.Solve(CancellationToken.None);
                sw2.Stop();

                return (sw1.ElapsedMilliseconds, sw2.ElapsedMilliseconds,
                        r1.length, r2.length);
            });

            long t1 = result.Item1;
            long t2 = result.Item2;
            double l1 = result.Item3;
            double l2 = result.Item4;
            double speedup = (double)t1 / Math.Max(1, t2);

            TbBenchResult.Text =
                $"Ядер: {Environment.ProcessorCount}\n" +
                $"Seed: {fixedSeed}\n" +
                $"──────────────\n" +
                $"1 поток : {t1,6} мс, len = {l1:F2}\n" +
                $"{threads} пот.  : {t2,6} мс, len = {l2:F2}\n" +
                $"Ускорение: {speedup:F2}x\n" +
                $"Совпадение решений: {(Math.Abs(l1 - l2) < 1e-6 ? "да" : "НЕТ")}";

            TbStatus.Text = "Замер завершён";
            BtnStart.IsEnabled = true;
            BtnBench.IsEnabled = true;
        }

        private bool TryReadParams(out int n, out int pop, out int gens,
                                   out int threads, out double pm, out int? seed)
        {
            n = pop = gens = threads = 0;
            pm = 0;
            seed = null;

            int cores = Environment.ProcessorCount;

            try
            {
                n = int.Parse(TbCities.Text);
                pop = int.Parse(TbPop.Text);
                gens = int.Parse(TbGens.Text);
                threads = int.Parse(TbThreads.Text);
                pm = double.Parse(TbMut.Text, CultureInfo.InvariantCulture);

                if (!string.IsNullOrWhiteSpace(TbSeed.Text))
                    seed = int.Parse(TbSeed.Text);
            }
            catch
            {
                MessageBox.Show("Проверь параметры — где-то не число.");
                return false;
            }

            if (n < 2) { MessageBox.Show("Городов должно быть ≥ 2"); return false; }
            if (pop < 4) { MessageBox.Show("Популяция должна быть ≥ 4"); return false; }
            if (gens < 1) { MessageBox.Show("Поколений должно быть ≥ 1"); return false; }
            if (pm < 0 || pm > 1)
            {
                MessageBox.Show("Вероятность мутации — от 0 до 1");
                return false;
            }

            if (threads < 1)
            {
                threads = 1;
                TbThreads.Text = "1";
            }
            if (threads > cores)
            {
                MessageBox.Show(
                    $"На этом компьютере только {cores} логических ядер.\n" +
                    $"Число потоков ограничено до {cores}.",
                    "Ограничение потоков",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                threads = cores;
                TbThreads.Text = cores.ToString();
            }

            return true;
        }
        
        private void OnGenerationUI(GenerationInfo info)
        {
            _history.Add(info);
           // TbOrder.Text += $"\ngen {info.Generation,4}: best = {info.BestLength,8:F2}  avg = {info.AvgLength,8:F2}";
            TbStatus.Text = $"Поколение {info.Generation}  " +
                            $"(best {info.BestLength:F0}, avg {info.AvgLength:F0})";
            TbBest.Text = $"Лучшая длина: {info.BestLength:F2}";
            DrawChart();
        }

        // ============================================================
        //  Рисование карты
        // ============================================================
        private void DrawCities()
        {
            CanvasMap.Children.Clear();
            if (_coords == null) return;

            var (sx, sy, ox, oy) = GetMapTransform();

            for (int i = 0; i < _coords.GetLength(0); i++)
            {
                double x = ox + _coords[i, 0] * sx;
                double y = oy + _coords[i, 1] * sy;

                var dot = new Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = Brushes.Red,
                    Stroke = Brushes.DarkRed,
                    StrokeThickness = 1
                };
                Canvas.SetLeft(dot, x - 6);
                Canvas.SetTop(dot, y - 6);
                CanvasMap.Children.Add(dot);

                var label = new TextBlock
                {
                    Text = i.ToString(),
                    FontWeight = FontWeights.Bold,
                    Foreground = Brushes.Black
                };
                Canvas.SetLeft(label, x + 8);
                Canvas.SetTop(label, y - 8);
                CanvasMap.Children.Add(label);
            }
        }

        private void DrawRoute(int[] order)
        {
            if (_coords == null || order == null) return;

            DrawCities();
            var (sx, sy, ox, oy) = GetMapTransform();

            var poly = new Polyline
            {
                Stroke = Brushes.RoyalBlue,
                StrokeThickness = 2
            };
            foreach (int c in order)
                poly.Points.Add(new Point(ox + _coords[c, 0] * sx,
                                          oy + _coords[c, 1] * sy));

            int first = order[0];
            poly.Points.Add(new Point(ox + _coords[first, 0] * sx,
                                      oy + _coords[first, 1] * sy));

            CanvasMap.Children.Add(poly);
        }

        private void RedrawAll()
        {
            if (_lastOrder != null) DrawRoute(_lastOrder);
            else DrawCities();
            DrawChart();
        }

        private (double sx, double sy, double ox, double oy) GetMapTransform()
        {
            double W = CanvasMap.ActualWidth;
            double H = CanvasMap.ActualHeight;
            const double margin = 30;
            return ((W - 2 * margin) / 1000.0,
                    (H - 2 * margin) / 1000.0,
                    margin, margin);
        }

        // ============================================================
        //  График best
        // ============================================================
        private void DrawChart()
        {
            CanvasChart.Children.Clear();
            if (CanvasChart.ActualWidth < 10 || CanvasChart.ActualHeight < 10) return;
            if (_maxGenerations < 1) return;

            double W = CanvasChart.ActualWidth;
            double H = CanvasChart.ActualHeight;

            const double margin = 40;
            double plotW = W - 2 * margin;
            double plotH = H - 2 * margin;

            // Ось X — фиксирована от 0 до _maxGenerations
            int maxGen = _maxGenerations;

            // Ось Y — по имеющимся данным best
            double minY, maxY;
            if (_history.Count == 0)
            {
                minY = 0;
                maxY = 1;
            }
            else
            {
                minY = _history.Min(h => h.BestLength);
                maxY = _history.Max(h => h.BestLength);
            }
            if (maxY - minY < 1e-6) maxY = minY + 1;

            // Оси
            CanvasChart.Children.Add(new Line
            {
                X1 = margin,
                Y1 = H - margin,
                X2 = W - margin,
                Y2 = H - margin,
                Stroke = Brushes.Black,
                StrokeThickness = 1
            });
            CanvasChart.Children.Add(new Line
            {
                X1 = margin,
                Y1 = margin,
                X2 = margin,
                Y2 = H - margin,
                Stroke = Brushes.Black,
                StrokeThickness = 1
            });

            // Подписи
            if (_history.Count > 0)
                AddText($"best = {_history.Last().BestLength:F2}", margin + 5, 5, Brushes.DarkGreen);

            AddText($"{maxY:F0}", 5, margin - 5, Brushes.Black);
            AddText($"{minY:F0}", 5, H - margin - 10, Brushes.Black);
            AddText("0", margin - 4, H - margin + 4, Brushes.Black);
            AddText($"{maxGen}", W - margin - 25, H - margin + 4, Brushes.Black);

            // Кривая best (только по уже имеющимся точкам)
            if (_history.Count == 0) return;

            var bestLine = new Polyline { Stroke = Brushes.Green, StrokeThickness = 2 };
            foreach (var h in _history)
            {
                double x = margin + plotW * h.Generation / maxGen;
                double y = H - margin - plotH * (h.BestLength - minY) / (maxY - minY);
                bestLine.Points.Add(new Point(x, y));
            }
            CanvasChart.Children.Add(bestLine);
        }

        private void AddText(string text, double x, double y, Brush color)
        {
            var tb = new TextBlock { Text = text, Foreground = color, FontSize = 11 };
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            CanvasChart.Children.Add(tb);
        }
    }
}