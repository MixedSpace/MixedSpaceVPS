using System;
using MixedSpace.XR;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MixedSpace
{
    /// <summary>
    /// 场景局部扩展引用，与可复用策略资产分离；运行时接收数组副本。
    /// Scene-local extension references kept separate from reusable policy assets; the runtime receives array copies.
    /// </summary>
    [Serializable]
    public sealed class MixedSpaceScenePorts
    {
        [SerializeField, MixedSpaceComponentContract(typeof(IMixedSpaceLocalizationBinding))]
        private Object[] m_LocalizationViews = Array.Empty<Object>();
        [SerializeField, MixedSpaceComponentContract(typeof(IMixedSpaceAlignmentSolver))]
        private Object m_AlignmentSolverOverride;
        [SerializeField, MixedSpaceComponentContract(typeof(IMixedSpaceLocalizationProvider))]
        private Object[] m_ExtensionProviders = Array.Empty<Object>();

        internal Object[] CopyViews() => (Object[])(m_LocalizationViews ?? Array.Empty<Object>()).Clone();
        internal Object[] CopyProviders() => (Object[])(m_ExtensionProviders ?? Array.Empty<Object>()).Clone();
        internal Object Solver => m_AlignmentSolverOverride;

        internal MixedSpaceScenePorts(Object[] views, Object solver, Object[] providers)
        {
            m_LocalizationViews = (Object[])(views ?? Array.Empty<Object>()).Clone();
            m_AlignmentSolverOverride = solver;
            m_ExtensionProviders = (Object[])(providers ?? Array.Empty<Object>()).Clone();
        }

        public MixedSpaceScenePorts() { }
    }
}
