using System.Threading.Tasks;

namespace MixedSpace.XR
{
    public enum MixedSpaceSessionCommand
    {
        Begin,
        Suspend,
        Continue,
        Reset,
        Stop,
        RunOneCycle,
        AllowLocalization,
        BlockLocalization
    }

    public enum MixedSpaceSessionPhase
    {
        Stopped,
        Running,
        Suspended,
        Resetting
    }

    public sealed class MixedSpaceSessionTransition
    {
        public MixedSpaceSessionPhase Previous { get; }
        public MixedSpaceSessionPhase Current { get; }
        public MixedSpaceSessionCommand Command { get; }

        public MixedSpaceSessionTransition(
            MixedSpaceSessionPhase previous,
            MixedSpaceSessionPhase current,
            MixedSpaceSessionCommand command)
        {
            Previous = previous;
            Current = current;
            Command = command;
        }
    }

    public interface IMixedSpaceSessionObserver
    {
        void Observe(MixedSpaceSessionTransition transition);
    }

    /// <summary>
    /// 面向命令的会话接口，由会话管理生命周期和合法状态转移。
    /// Command-oriented session interface that manages lifecycle and valid state transitions.
    /// </summary>
    public interface IMixedSpaceSpatialSession
    {
        MixedSpaceSessionPhase Phase { get; }
        bool ManualLocalizationControlEnabled { get; }
        IMixedSpaceLocalizationReadModel Localizations { get; }

        Task DispatchAsync(MixedSpaceSessionCommand command);
    }
}
