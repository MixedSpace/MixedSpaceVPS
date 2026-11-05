using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 一个引擎周期的不可变输出；提供器结果已快照化，使用方不能在帧完成后修改引擎状态。
    /// Immutable output of one engine cycle. Provider outcomes are snapshotted so
    /// consumers cannot mutate engine state after a frame has completed.
    /// </summary>
    public sealed class MixedSpaceLocalizationBatch
    {
        private static readonly MixedSpaceLocalizationBatch s_Empty =
            new MixedSpaceLocalizationBatch(Array.Empty<MixedSpaceLocalizationOutcome>());

        public MixedSpaceLocalizationBatch(
            IEnumerable<MixedSpaceLocalizationOutcome> outcomes)
        {
            Outcomes = new System.Collections.ObjectModel.ReadOnlyCollection<
                MixedSpaceLocalizationOutcome>(
                (outcomes ?? Enumerable.Empty<MixedSpaceLocalizationOutcome>())
                .Where(outcome => outcome != null)
                .ToArray());
        }

        public IReadOnlyList<MixedSpaceLocalizationOutcome> Outcomes { get; }

        public static MixedSpaceLocalizationBatch Empty => s_Empty;
    }

    /// <summary>
    /// 不可变的提供器结果，未命中无观测，成功必含提供器无关观测值。
    /// Immutable provider outcome. A miss carries no observation; a located
    /// outcome always carries a provider-neutral observation value.
    /// </summary>
    public sealed class MixedSpaceLocalizationOutcome
    {
        private MixedSpaceLocalizationOutcome(
            bool success,
            MixedSpaceLocalizationObservation observation)
        {
            Success = success;
            Observation = observation;
        }

        public bool Success { get; }
        public MixedSpaceLocalizationObservation Observation { get; }

        public static MixedSpaceLocalizationOutcome Missed()
        {
            return new MixedSpaceLocalizationOutcome(false, null);
        }

        public static MixedSpaceLocalizationOutcome Located(
            MixedSpaceLocalizationObservation observation)
        {
            if (observation == null)
                throw new ArgumentNullException(nameof(observation));
            return new MixedSpaceLocalizationOutcome(true, observation);
        }
    }

    /// <summary>
    /// 普通 C# 定位协调器，不持有 Unity 生命周期；计划和帧计算共用状态闸门，地图变化不会与活动周期竞争。
    /// Coordinates localization providers without owning a Unity component or
    /// lifecycle. Plans and frame processing pass through one state gate, so map
    /// changes can never race an in-flight provider cycle.
    /// </summary>
    public sealed class MixedSpaceLocalizationEngine : IMixedSpaceLocalizationCoordinator
    {
        private IMixedSpaceLocalizationProvider[] m_Providers =
            Array.Empty<IMixedSpaceLocalizationProvider>();
        private readonly List<IMixedSpaceLocalizationProvider> m_ActiveProviders =
            new List<IMixedSpaceLocalizationProvider>();
        private readonly SemaphoreSlim m_StateGate = new SemaphoreSlim(1, 1);
        private readonly object m_FrameCancellationSync = new object();
        private CancellationTokenSource m_ActiveFrameCancellation;

        public IReadOnlyList<IMixedSpaceLocalizationProvider> Providers => m_Providers;

        public void InstallProviders(IEnumerable<IMixedSpaceLocalizationProvider> providers)
        {
            if (m_ActiveProviders.Count > 0)
            {
                throw new InvalidOperationException(
                    "Localization providers cannot be replaced while the runtime is configured.");
            }

            IMixedSpaceLocalizationProvider[] candidates =
                (providers ?? Enumerable.Empty<IMixedSpaceLocalizationProvider>())
                .Where(provider => !provider.IsNullOrDead())
                .ToArray();
            IMixedSpaceLocalizationProvider invalid = candidates.FirstOrDefault(provider =>
                string.IsNullOrWhiteSpace(provider.ProviderId));
            if (invalid != null)
            {
                throw new InvalidOperationException(
                    $"Localization provider {invalid.GetType().Name} has no stable ProviderId.");
            }

            string duplicateId = candidates
                .GroupBy(provider => provider.ProviderId, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .FirstOrDefault();
            if (duplicateId != null)
            {
                throw new InvalidOperationException(
                    $"More than one localization provider uses id '{duplicateId}'.");
            }

            m_Providers = candidates;
        }

        public bool TryResolveProvider(
            string providerId,
            out IMixedSpaceLocalizationProvider provider)
        {
            provider = m_Providers.FirstOrDefault(candidate =>
                string.Equals(candidate.ProviderId, providerId, StringComparison.Ordinal));
            return provider != null;
        }

        public async Task<bool> ApplyPlanAsync(MixedSpaceLocalizationPlan plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (plan.CancelActiveCycle)
                RequestActiveFrameCancellation();

            await m_StateGate.WaitAsync();
            try
            {
                if (!TargetsAreInstalled(plan.Additions) ||
                    !TargetsAreInstalled(plan.Removals))
                {
                    return false;
                }

                MixedSpaceLogger.Log("Applying MixedSpace localization plan");
                var additionsApplied = new List<AppliedAddition>();

                if (plan.Additions != null)
                {
                    foreach (IMixedSpaceLocalizationProvider provider in m_Providers)
                    {
                        bool hasAdditions = plan.Additions.TryGetValue(
                            provider.ProviderId,
                            out IReadOnlyList<MixedSpaceMapDefinition> maps);
                        bool shouldActivate = provider.ActivationMode switch
                        {
                            MixedSpaceProviderActivationMode.WhenNecessary => hasAdditions,
                            MixedSpaceProviderActivationMode.Always => true,
                            _ => false
                        };
                        MixedSpaceLogger.Log(
                            $"ApplyPlan: provider={provider.ProviderId} " +
                            $"mode={provider.ActivationMode} hasAdditions={hasAdditions} " +
                            $"activate={shouldActivate} mapCount={(maps?.Count ?? 0)} " +
                            $"planKeys=[{string.Join(",", plan.Additions.Keys)}]", MixedSpaceLogger.LoggingLevel.Verbose);
                        if (!shouldActivate)
                            continue;

                        bool wasActive = m_ActiveProviders.Contains(provider);
                        bool applied;
                        try
                        {
                            applied = await provider.ApplyAsync(
                                new MixedSpaceProviderPlan(additions: maps));
                        }
                        catch (Exception exception)
                        {
                            MixedSpaceLogger.LogError(
                                $"Provider '{provider.ProviderId}' rejected the plan: " +
                                exception.Message);
                            await RollBackAdditionsAsync(
                                additionsApplied,
                                new AppliedAddition(provider, maps, wasActive));
                            return false;
                        }

                        var appliedAddition = new AppliedAddition(provider, maps, wasActive);
                        if (!applied)
                        {
                            MixedSpaceLogger.LogError(
                                $"Provider '{provider.ProviderId}' could not apply the plan.");
                            await RollBackAdditionsAsync(
                                additionsApplied,
                                appliedAddition);
                            return false;
                        }

                        additionsApplied.Add(appliedAddition);
                    }
                }

                foreach (AppliedAddition addition in additionsApplied)
                {
                    if (!addition.WasActive &&
                        !m_ActiveProviders.Contains(addition.Provider))
                    {
                        m_ActiveProviders.Add(addition.Provider);
                    }
                }

                if (plan.Removals != null)
                {
                    foreach (KeyValuePair<
                        string,
                        IReadOnlyList<MixedSpaceMapDefinition>> removal in plan.Removals)
                    {
                        if (TryResolveProvider(
                            removal.Key,
                            out IMixedSpaceLocalizationProvider provider))
                        {
                            await ApplyRemovalsAsync(provider, removal.Value);
                        }
                    }
                }

                return true;
            }
            finally
            {
                m_StateGate.Release();
            }
        }

        /// <summary>
        /// 将一帧交给全部活动提供器，不公开或持久保存提供器任务注册表；调用状态只在本方法中存在，帧结束后丢弃。
        /// Processes one camera frame through every active provider. There is no
        /// public or persistent provider-task registry; all invocation state is
        /// local to this method and is discarded at the end of the frame.
        /// </summary>
        public async Task<MixedSpaceLocalizationBatch> EvaluateFrameAsync(
            IMixedSpaceCameraCapture capture,
            CancellationToken cancellationToken)
        {
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            cancellationToken.ThrowIfCancellationRequested();

            await m_StateGate.WaitAsync(cancellationToken);

            using var frameCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            SetActiveFrameCancellation(frameCancellation);
            try
            {
                var invocations = new List<ProviderInvocation>();
                foreach (IMixedSpaceLocalizationProvider provider in m_ActiveProviders)
                {
                    try
                    {
                        Task<MixedSpaceLocalizationOutcome> task = provider.EvaluateAsync(
                            capture,
                            frameCancellation.Token);
                        if (task == null)
                        {
                            MixedSpaceLogger.LogError(
                                $"Provider '{provider.ProviderId}' returned no frame task.");
                            continue;
                        }
                        invocations.Add(new ProviderInvocation(provider, task));
                    }
                    catch (Exception exception)
                    {
                        MixedSpaceLogger.LogError(
                            $"Provider '{provider.ProviderId}' failed to start: " +
                            exception.Message);
                    }
                }

                try
                {
                    await Task.WhenAll(invocations.Select(invocation => invocation.Task));
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 计划变更或关闭请求主动取消了当前帧。
                    // A plan or shutdown request cancelled this frame intentionally.
                    return EmptyResults();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    // 逐个检查调用结果，某后端失败不应丢弃其他提供器的成功结果。
                    // Inspect each invocation below so successful providers still contribute results when another backend fails.
                    MixedSpaceLogger.LogError(
                        $"One or more localization providers failed: {exception.Message}");
                }

                var outcomes = new List<MixedSpaceLocalizationOutcome>();
                foreach (ProviderInvocation invocation in invocations)
                {
                    if (invocation.Task.Status == TaskStatus.RanToCompletion)
                    {
                        if (invocation.Task.Result != null)
                            outcomes.Add(invocation.Task.Result);
                    }
                    else if (invocation.Task.IsFaulted)
                    {
                        string message = invocation.Task.Exception?
                            .GetBaseException().Message ?? "Unknown error";
                        MixedSpaceLogger.LogError(
                            $"Provider '{invocation.Provider.ProviderId}' failed: {message}");
                    }
                }

                return new MixedSpaceLocalizationBatch(outcomes);
            }
            finally
            {
                ClearActiveFrameCancellation(frameCancellation);
                m_StateGate.Release();
            }
        }

        public async Task ShutdownAsync()
        {
            RequestActiveFrameCancellation();
            await m_StateGate.WaitAsync();
            try
            {
                foreach (IMixedSpaceLocalizationProvider provider in
                    m_ActiveProviders.ToArray())
                {
                    try
                    {
                        await provider.ReleaseAsync();
                    }
                    catch (Exception exception)
                    {
                        MixedSpaceLogger.LogError(
                            $"Provider '{provider.ProviderId}' shutdown failed: " +
                            exception.Message);
                    }
                }
                m_ActiveProviders.Clear();
            }
            finally
            {
                m_StateGate.Release();
            }
        }

        private bool TargetsAreInstalled(
            IReadOnlyDictionary<
                string,
                IReadOnlyList<MixedSpaceMapDefinition>> targets)
        {
            if (targets == null)
                return true;

            string unknown = targets.Keys.FirstOrDefault(providerId =>
                string.IsNullOrWhiteSpace(providerId) ||
                !TryResolveProvider(providerId, out _));
            if (unknown == null)
                return true;

            MixedSpaceLogger.LogError(
                $"Localization plan references unavailable provider '{unknown}'.");
            return false;
        }

        private async Task ApplyRemovalsAsync(
            IMixedSpaceLocalizationProvider provider,
            IReadOnlyList<MixedSpaceMapDefinition> maps)
        {
            bool wasActive = m_ActiveProviders.Contains(provider);
            if (!wasActive)
            {
                MixedSpaceLogger.LogWarning(
                    $"Provider '{provider.ProviderId}' is not active during map removal.");
            }

            bool remainsActive = await provider.ApplyAsync(
                new MixedSpaceProviderPlan(removals: maps));
            if (!remainsActive &&
                provider.ActivationMode == MixedSpaceProviderActivationMode.WhenNecessary &&
                wasActive)
            {
                await provider.ReleaseAsync();
                m_ActiveProviders.Remove(provider);
            }
        }

        private static async Task RollBackAdditionsAsync(
            IList<AppliedAddition> completed,
            AppliedAddition failed)
        {
            var rollback = new List<AppliedAddition>(completed) { failed };
            for (int index = rollback.Count - 1; index >= 0; index--)
            {
                AppliedAddition addition = rollback[index];
                try
                {
                    if (addition.Maps is { Count: > 0 })
                    {
                        await addition.Provider.ApplyAsync(
                            new MixedSpaceProviderPlan(removals: addition.Maps));
                    }
                    if (!addition.WasActive)
                        await addition.Provider.ReleaseAsync();
                }
                catch (Exception exception)
                {
                    MixedSpaceLogger.LogError(
                        $"Provider '{addition.Provider.ProviderId}' rollback failed: " +
                        exception.Message);
                }
            }
        }

        private void RequestActiveFrameCancellation()
        {
            lock (m_FrameCancellationSync)
            {
                if (m_ActiveFrameCancellation == null)
                    return;
                try
                {
                    m_ActiveFrameCancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }

        private void SetActiveFrameCancellation(CancellationTokenSource source)
        {
            lock (m_FrameCancellationSync)
                m_ActiveFrameCancellation = source;
        }

        private void ClearActiveFrameCancellation(CancellationTokenSource source)
        {
            lock (m_FrameCancellationSync)
            {
                if (ReferenceEquals(m_ActiveFrameCancellation, source))
                    m_ActiveFrameCancellation = null;
            }
        }

        private static MixedSpaceLocalizationBatch EmptyResults()
        {
            return MixedSpaceLocalizationBatch.Empty;
        }

        private sealed class ProviderInvocation
        {
            public ProviderInvocation(
                IMixedSpaceLocalizationProvider provider,
                Task<MixedSpaceLocalizationOutcome> task)
            {
                Provider = provider;
                Task = task;
            }

            public IMixedSpaceLocalizationProvider Provider { get; }
            public Task<MixedSpaceLocalizationOutcome> Task { get; }
        }

        private readonly struct AppliedAddition
        {
            public AppliedAddition(
                IMixedSpaceLocalizationProvider provider,
                IReadOnlyList<MixedSpaceMapDefinition> maps,
                bool wasActive)
            {
                Provider = provider;
                Maps = maps;
                WasActive = wasActive;
            }

            public IMixedSpaceLocalizationProvider Provider { get; }
            public IReadOnlyList<MixedSpaceMapDefinition> Maps { get; }
            public bool WasActive { get; }
        }
    }

}
