using System.Collections.Concurrent;
using System.Diagnostics;

namespace LivePhotoConvert.Core.Pipeline;

/// <summary>
/// 并行处理一批条目：单个条目的异常按类型归类为失败原因而不中断整批，取消时返回已完成部分的报告。
/// </summary>
public static class BatchRunner
{
    /// <param name="items">待处理条目</param>
    /// <param name="sourceOf">条目的主源文件，用于失败记录与进度显示</param>
    /// <param name="process">处理单个条目</param>
    /// <param name="parallelism">最大并行数</param>
    /// <param name="progress">进度回调</param>
    /// <param name="resolvedOutcomes">处理前已确定结果的条目（例如配对校验未通过而跳过的），计入总数</param>
    /// <param name="cancellationToken">取消令牌</param>
    public static async Task<BatchReport> RunAsync<T>(
        IReadOnlyList<T> items,
        Func<T, string> sourceOf,
        Func<T, CancellationToken, Task<ItemOutcome>> process,
        int parallelism,
        IProgress<BatchProgress>? progress,
        IReadOnlyList<ItemOutcome>? resolvedOutcomes = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcomes = new ConcurrentQueue<ItemOutcome>(resolvedOutcomes ?? []);
        var preresolved = outcomes.Count;
        var total = items.Count + preresolved;
        var completed = preresolved;
        var canceled = false;

        try
        {
            await Parallel.ForEachAsync(
                items,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken },
                async (item, token) =>
                {
                    var source = sourceOf(item);
                    ItemOutcome outcome;
                    try
                    {
                        outcome = await process(item, token);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        outcome = ItemOutcome.Failed(source, ex);
                    }

                    outcomes.Enqueue(outcome);
                    progress?.Report(new BatchProgress(Interlocked.Increment(ref completed), total, Path.GetFileName(source), preresolved));
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            canceled = true;
        }

        return new BatchReport([.. outcomes], stopwatch.Elapsed, canceled);
    }
}
