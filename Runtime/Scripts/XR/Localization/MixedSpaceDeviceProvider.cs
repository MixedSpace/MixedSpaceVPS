using System;
using UnityEngine;

namespace MixedSpace.XR
{
    /// <summary>
    /// 内置定位服务共享的求解策略。
    /// Solver strategy shared by the built-in localization services.
    /// </summary>
    public enum MixedSpaceSolverType
    {
        Default = 0,
        Lean = 1,
        Prior = 2
    }

}
