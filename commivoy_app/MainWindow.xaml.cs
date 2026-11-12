using commivoy;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace commivoy_app
{
    public partial class MainWindow : Window
    {
        private CancellationTokenSource _cts;
        private double[,] _coords;
        private readonly List<GenerationInfo> _history = new List<GenerationInfo>();
        private int[] _lastOrder;
        private int _maxGenerations = 1;
        private readonly string _logFilePath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cluster_run.log");

        public MainWindow()
        {
            InitializeComponent();
            LbIps.Items.Add("http://localhost:5001");
            LbIps.Items.Add("http://localhost:5002");

            int cores = Environment.ProcessorCount;
            TbThreads.Text = cores.ToString();
            TbThreads.ToolTip = $"На этом компьютере доступно логических ядер: {cores}";
            TbSeed.ToolTip = "Единый Master Seed для всей распределённой задачи.";

            CanvasMap.SizeChanged += (s, e) => RedrawAll();
            CanvasChart.SizeChanged += (s, e) => DrawChart();
        }

        private void WriteLog(string message)
        {
            try
            {
                string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
                File.AppendAllText(_logFilePath, line);
            }
            catch { /* Игнорируем ошибки блокировки файла лога */ }
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            WriteLog("Пользователь нажал кнопку Остановить.");
            TbStatus.Text = "Остановка вычислений... Сохранение достигнутого результата.";
        }

        private void BtnAddIp_Click(object sender, RoutedEventArgs e)
        {
            string ip = TbNewIp.Text.Trim();
            if (!string.IsNullOrEmpty(ip) && !LbIps.Items.Contains(ip))
                LbIps.Items.Add(ip);
        }

        private void BtnRemoveIp_Click(object sender, RoutedEventArgs e)
        {
            if (LbIps.SelectedItem != null)
                LbIps.Items.Remove(LbIps.SelectedItem);
            else
                MessageBox.Show("Выберите сервис из списка, чтобы удалить его.", "Внимание", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void LbIps_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (LbIps.SelectedItem != null)
            {
                string text = (LbIps.SelectedItem is ListBoxItem item) ? item.Content.ToString() : LbIps.SelectedItem.ToString();
                TbNewIp.Text = text;
                LbIps.Items.Remove(LbIps.SelectedItem);
                TbNewIp.Focus();
                TbNewIp.CaretIndex = TbNewIp.Text.Length;
            }
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadParams(out int n, out int popSize, out int gens, out int threads, out double pm, out int? seed))
                return;

            var configuredIps = GetActiveIpList();
            if (configuredIps.Count == 0)
            {
                MessageBox.Show("Добавьте хотя бы один IP-адрес сервиса!");
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

            BtnStart.IsEnabled = false;
            BtnStop.IsEnabled = true;
            BtnBench.IsEnabled = false;

            WriteLog("=== ЗАПУСК НОВОЙ РАСПРЕДЕЛЁННОЙ ЗАДАЧИ ===");
            TbStatus.Text = "Проверка подключения к сервисам (Ping)...";

            // 1. Живая проверка коннекта ко всем IP
            var aliveIps = new List<string>();
            foreach (var ip in configuredIps)
            {
                try
                {
                    using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    pingCts.CancelAfter(1500);
                    var pingResp = await client.GetAsync($"{ip}/api/Ga/ping", pingCts.Token);
                    if (pingResp.IsSuccessStatusCode)
                    {
                        aliveIps.Add(ip);
                        WriteLog($"[CONNECT OK] Сервис {ip} доступен.");
                    }
                    else
                    {
                        WriteLog($"[CONNECT FAIL] Сервис {ip} вернул код {pingResp.StatusCode}.");
                    }
                }
                catch (Exception ex)
                {
                    WriteLog($"[CONNECT FAIL] Сервис {ip} недоступен: {ex.Message}");
                }
            }

            if (aliveIps.Count == 0)
            {
                TbStatus.Text = "Ошибка: ни один сервис не ответил на Ping.";
                WriteLog("[ABORT] Нет доступных серверов для запуска.");
                BtnStart.IsEnabled = true;
                BtnStop.IsEnabled = false;
                BtnBench.IsEnabled = true;
                return;
            }

            // 2. Единый Master Seed для всей задачи
            int masterSeed = seed ?? Environment.TickCount;
            string taskId = Guid.NewGuid().ToString("N");
            WriteLog($"Параметры: TaskId={taskId}, Cities={n}, Pop={popSize}, Gens={gens}, MasterSeed={masterSeed}, Nodes={aliveIps.Count}");

            var gen = new MatrixGenerator();
            _coords = gen.GenerateCoords(n, masterSeed);
            double[,] d = gen.GenerateMatrix(_coords, n);

            double[][] matrixToSend = new double[n][];
            for (int i = 0; i < n; i++)
            {
                matrixToSend[i] = new double[n];
                for (int j = 0; j < n; j++) matrixToSend[i][j] = d[i, j];
            }

            // Инициализация единой стартовой популяции
            int[][] pop = GeneticSolver.CreateInitialPopulation(n, popSize, masterSeed);
            double[] fit = new double[popSize];
            for (int i = 0; i < popSize; i++)
                fit[i] = GeneticSolver.PathLength(pop[i], d);

            _maxGenerations = gens;
            _history.Clear();
            _lastOrder = null;
            CanvasChart.Children.Clear();
            DrawCities();

            int[] bestOrder = null;
            double bestLen = double.MaxValue;
            const int eliteCount = 2;

            var initializedNodes = new HashSet<string>();
            var swTotal = Stopwatch.StartNew();
            var uiRedrawWatch = Stopwatch.StartNew();

            TbStatus.Text = $"Подключено узлов: {aliveIps.Count}/{configuredIps.Count}. Старт вычислений...";

            try
            {
                // 3. Пошаговая эволюция: каждое поколение делится на куски между живыми IP
                for (int g = 0; g < gens; g++)
                {
                    if (token.IsCancellationRequested) break;

                    // Находим лучшую особь текущего поколения
                    int bi = 0;
                    for (int i = 1; i < popSize; i++)
                        if (fit[i] < fit[bi]) bi = i;

                    if (fit[bi] < bestLen)
                    {
                        bestLen = fit[bi];
                        bestOrder = (int[])pop[bi].Clone();
                        _lastOrder = bestOrder;
                        WriteLog($"[NEW BEST] Gen={g}, Length={bestLen:F2}");
                    }

                    var genInfo = new GenerationInfo
                    {
                        Generation = g,
                        BestLength = bestLen,
                        AvgLength = fit.Average(),
                        BestOrder = (int[])bestOrder.Clone()
                    };
                    _history.Add(genInfo);

                    // Обновляем графику в реальном времени (не чаще 20 раз/сек, чтобы не тормозить UI)
                    if (uiRedrawWatch.ElapsedMilliseconds >= 50 || g == gens - 1)
                    {
                        TbStatus.Text = $"Узлов: {aliveIps.Count} | Поколение {g + 1}/{gens} | Лучшая: {bestLen:F2}";
                        TbBest.Text = $"Лучшая длина: {bestLen:F2}";
                        TbOrder.Text = "Маршрут: " + string.Join(" → ", bestOrder);
                        DrawRoute(bestOrder);
                        DrawChart();
                        uiRedrawWatch.Restart();
                    }

                    // Сортируем индексы и переносим элиту
                    var sortedIdx = Enumerable.Range(0, popSize).OrderBy(i => fit[i]).ToList();
                    int elites = Math.Min(eliteCount, sortedIdx.Count);

                    var nextPop = new int[popSize][];
                    var nextFit = new double[popSize];
                    for (int k = 0; k < elites; k++)
                    {
                        nextPop[k] = (int[])pop[sortedIdx[k]].Clone();
                        nextFit[k] = fit[sortedIdx[k]];
                    }

                    // Нарезаем диапазон потомков [elites .. popSize) между живыми серверами
                    int totalWork = popSize - elites;
                    int nodeCount = aliveIps.Count;
                    int threadsPerNode = Math.Max(1, threads / nodeCount);

                    var sliceTasks = new List<Task<(string ip, int start, int end, SliceTaskResult res, Exception err)>>();

                    for (int k = 0; k < nodeCount; k++)
                    {
                        int startIdx = elites + (totalWork * k) / nodeCount;
                        int endIdx = elites + (totalWork * (k + 1)) / nodeCount;
                        if (startIdx >= endIdx) continue;

                        string targetIp = aliveIps[k];
                        bool needsMatrix = !initializedNodes.Contains(targetIp);

                        var req = new SliceTaskRequest
                        {
                            TaskId = taskId,
                            Distances = needsMatrix ? matrixToSend : null,
                            Population = pop,
                            Fitness = fit,
                            MasterSeed = masterSeed,
                            Generation = g,
                            StartIndex = startIdx,
                            EndIndex = endIdx,
                            MutationProb = pm,
                            Threads = threadsPerNode
                        };

                        sliceTasks.Add(SendSliceWithRetryAsync(client, targetIp, req, matrixToSend, initializedNodes, token));
                    }

                    var sliceResponses = await Task.WhenAll(sliceTasks);

                    // Собираем результаты срезов и обрабатываем возможную поломку узла (Failover)
                    foreach (var item in sliceResponses)
                    {
                        if (item.err == null && item.res != null)
                        {
                            for (int idx = 0; idx < item.res.Offspring.Length; idx++)
                            {
                                nextPop[item.start + idx] = item.res.Offspring[idx];
                                nextFit[item.start + idx] = item.res.OffspringFitness[idx];
                            }
                        }
                        else if (!token.IsCancellationRequested)
                        {
                            // Узел упал во время работы! Логируем и пересчитываем его кусок на выжившем узле
                            WriteLog($"[NODE FAILURE] Узел {item.ip} отказал на поколении {g} (срез [{item.start}..{item.end})): {item.err?.Message}");
                            aliveIps.Remove(item.ip);
                            initializedNodes.Remove(item.ip);

                            if (aliveIps.Count > 0)
                            {
                                string backupIp = aliveIps[0];
                                WriteLog($"[FAILOVER] Перенос среза [{item.start}..{item.end}) на резервный узел {backupIp}");

                                var fallbackReq = new SliceTaskRequest
                                {
                                    TaskId = taskId,
                                    Distances = !initializedNodes.Contains(backupIp) ? matrixToSend : null,
                                    Population = pop,
                                    Fitness = fit,
                                    MasterSeed = masterSeed,
                                    Generation = g,
                                    StartIndex = item.start,
                                    EndIndex = item.end,
                                    MutationProb = pm,
                                    Threads = threads
                                };

                                var backup = await SendSliceWithRetryAsync(client, backupIp, fallbackReq, matrixToSend, initializedNodes, token);
                                if (backup.err == null && backup.res != null)
                                {
                                    for (int idx = 0; idx < backup.res.Offspring.Length; idx++)
                                    {
                                        nextPop[item.start + idx] = backup.res.Offspring[idx];
                                        nextFit[item.start + idx] = backup.res.OffspringFitness[idx];
                                    }
                                }
                                else
                                {
                                    throw new Exception("Все узлы кластера отказали во время переключения (Failover).");
                                }
                            }
                            else
                            {
                                throw new Exception("Не осталось ни одного активного сервера для продолжения.");
                            }
                        }
                    }

                    pop = nextPop;
                    fit = nextFit;
                }

                swTotal.Stop();

                if (token.IsCancellationRequested)
                {
                    TbStatus.Text = $"Остановлено на поколении {_history.Count}/{gens} за {swTotal.ElapsedMilliseconds} мс. Лучшая: {bestLen:F2}";
                    WriteLog($"[STOPPED] Выполнение прервано пользователем. Достигнутая длина: {bestLen:F2}");
                }
                else
                {
                    TbStatus.Text = $"Готово за {swTotal.ElapsedMilliseconds} мс (Узлов: {aliveIps.Count}, Seed: {masterSeed})";
                    WriteLog($"[COMPLETED] Задача завершена за {swTotal.ElapsedMilliseconds} мс. Итоговая длина: {bestLen:F2}");
                }

                if (bestOrder != null)
                {
                    TbBest.Text = $"Лучшая длина: {bestLen:F2}";
                    TbOrder.Text = "Маршрут: " + string.Join(" → ", bestOrder);
                    DrawRoute(bestOrder);
                    DrawChart();
                }
            }
            catch (OperationCanceledException)
            {
                TbStatus.Text = $"Остановлено пользователем. Сохранено поколений: {_history.Count}";
            }
            catch (Exception ex)
            {
                WriteLog($"[CRITICAL ERROR] {ex.Message}");
                TbStatus.Text = $"Сбой кластера (сохранено {_history.Count} пок.): {ex.Message}";
                MessageBox.Show($"Произошёл сбой: {ex.Message}\n\nДостигнутый к моменту сбоя маршрут сохранён на экране.\nПодробности в файле: {_logFilePath}",
                    "Отчёт об ошибке", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                BtnStart.IsEnabled = true;
                BtnStop.IsEnabled = false;
                BtnBench.IsEnabled = true;
            }
        }

        private async Task<(string ip, int start, int end, SliceTaskResult res, Exception err)> SendSliceWithRetryAsync(
            HttpClient client,
            string ip,
            SliceTaskRequest req,
            double[][] fullMatrix,
            HashSet<string> initializedNodes,
            CancellationToken token)
        {
            try
            {
                var response = await client.PostAsJsonAsync($"{ip}/api/Ga/evolve-slice", req, token);

                // Если сервер потерял кэш (например, перезапускался), досылаем матрицу
                if (response.StatusCode == HttpStatusCode.Conflict)
                {
                    req.Distances = fullMatrix;
                    response = await client.PostAsJsonAsync($"{ip}/api/Ga/evolve-slice", req, token);
                }

                if (!response.IsSuccessStatusCode)
                {
                    string errorBody = await response.Content.ReadAsStringAsync(token);
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode} ({response.StatusCode}): {errorBody}");
                }
                initializedNodes.Add(ip);

                var result = await response.Content.ReadFromJsonAsync<SliceTaskResult>(cancellationToken: token);
                return (ip, req.StartIndex, req.EndIndex, result, null);
            }
            catch (Exception ex)
            {
                return (ip, req.StartIndex, req.EndIndex, null, ex);
            }
        }

        private List<string> GetActiveIpList()
        {
            var list = new List<string>();
            foreach (var item in LbIps.Items)
            {
                string ip = (item is ListBoxItem lbi) ? lbi.Content.ToString() : item.ToString();
                if (!string.IsNullOrWhiteSpace(ip)) list.Add(ip.TrimEnd('/'));
            }
            return list;
        }

        // ============================================================
        //  Эталонный замер локально (1 поток vs N потоков)
        // ============================================================
        private async void BtnBench_Click(object sender, RoutedEventArgs e)
        {
            if (!TryReadParams(out int n, out int pop, out int gens, out int threads, out double pm, out int? seed))
                return;

            int fixedSeed = seed ?? 42;
            var gen = new MatrixGenerator();
            _coords = gen.GenerateCoords(n, fixedSeed);
            double[,] d = gen.GenerateMatrix(_coords, n);

            TbBenchResult.Text = "Идёт замер...";
            BtnStart.IsEnabled = false;
            BtnBench.IsEnabled = false;

            var result = await Task.Run(() =>
            {
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

                return (sw1.ElapsedMilliseconds, sw2.ElapsedMilliseconds, r1.length, r2.length);
            });

            double speedup = (double)result.Item1 / Math.Max(1, result.Item2);

            TbBenchResult.Text =
                $"Ядер: {Environment.ProcessorCount}\n" +
                $"Master Seed: {fixedSeed}\n" +
                $"──────────────\n" +
                $"1 поток : {result.Item1,6} мс, len = {result.Item3:F2}\n" +
                $"{threads} пот.  : {result.Item2,6} мс, len = {result.Item4:F2}\n" +
                $"Ускорение: {speedup:F2}x\n" +
                $"Совпадение: {(Math.Abs(result.Item3 - result.Item4) < 1e-6 ? "100% ДА" : "НЕТ")}";

            TbStatus.Text = "Локальный замер завершён";
            BtnStart.IsEnabled = true;
            BtnBench.IsEnabled = true;
        }

        private bool TryReadParams(out int n, out int pop, out int gens, out int threads, out double pm, out int? seed)
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
            if (pm < 0 || pm > 1) { MessageBox.Show("Вероятность мутации — от 0 до 1"); return false; }

            if (threads < 1) { threads = 1; TbThreads.Text = "1"; }
            if (threads > cores)
            {
                threads = cores;
                TbThreads.Text = cores.ToString();
            }
            return true;
        }

        // ============================================================
        //  Отрисовка карты и графика
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

                var dot = new Ellipse { Width = 12, Height = 12, Fill = Brushes.Red, Stroke = Brushes.DarkRed, StrokeThickness = 1 };
                Canvas.SetLeft(dot, x - 6);
                Canvas.SetTop(dot, y - 6);
                CanvasMap.Children.Add(dot);

                var label = new TextBlock { Text = i.ToString(), FontWeight = FontWeights.Bold, Foreground = Brushes.Black };
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

            var poly = new Polyline { Stroke = Brushes.RoyalBlue, StrokeThickness = 2 };
            foreach (int c in order)
                poly.Points.Add(new Point(ox + _coords[c, 0] * sx, oy + _coords[c, 1] * sy));
            poly.Points.Add(new Point(ox + _coords[order[0], 0] * sx, oy + _coords[order[0], 1] * sy));
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
            return ((W - 2 * margin) / 1000.0, (H - 2 * margin) / 1000.0, margin, margin);
        }

        private void DrawChart()
        {
            CanvasChart.Children.Clear();
            if (CanvasChart.ActualWidth < 10 || CanvasChart.ActualHeight < 10 || _maxGenerations < 1) return;

            double W = CanvasChart.ActualWidth;
            double H = CanvasChart.ActualHeight;
            const double margin = 40;
            double plotW = W - 2 * margin;
            double plotH = H - 2 * margin;

            double minY = _history.Count == 0 ? 0 : _history.Min(h => h.BestLength);
            double maxY = _history.Count == 0 ? 1 : _history.Max(h => h.BestLength);
            if (maxY - minY < 1e-6) maxY = minY + 1;

            CanvasChart.Children.Add(new Line { X1 = margin, Y1 = H - margin, X2 = W - margin, Y2 = H - margin, Stroke = Brushes.Black, StrokeThickness = 1 });
            CanvasChart.Children.Add(new Line { X1 = margin, Y1 = margin, X2 = margin, Y2 = H - margin, Stroke = Brushes.Black, StrokeThickness = 1 });

            if (_history.Count > 0)
                AddText($"best = {_history.Last().BestLength:F2}", margin + 5, 5, Brushes.DarkGreen);

            AddText($"{maxY:F0}", 5, margin - 5, Brushes.Black);
            AddText($"{minY:F0}", 5, H - margin - 10, Brushes.Black);
            AddText("0", margin - 4, H - margin + 4, Brushes.Black);
            AddText($"{_maxGenerations}", W - margin - 25, H - margin + 4, Brushes.Black);

            if (_history.Count == 0) return;

            var bestLine = new Polyline { Stroke = Brushes.Green, StrokeThickness = 2 };
            foreach (var h in _history)
            {
                double x = margin + plotW * h.Generation / _maxGenerations;
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