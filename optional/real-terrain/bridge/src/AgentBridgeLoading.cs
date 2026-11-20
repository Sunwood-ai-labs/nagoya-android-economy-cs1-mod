using ICities;

namespace SkylinesAgentBridge
{
    public sealed class AgentBridgeLoading : LoadingExtensionBase
    {
        public override void OnLevelLoaded(LoadMode mode)
        {
            NagoyaMapJob.LevelLoaded(mode);
            AgentBridge.Instance.OnLevelLoaded();
        }

        public override void OnLevelUnloading()
        {
            NagoyaMapJob.LevelUnloading();
            AgentBridge.Instance.OnLevelUnloading();
        }
    }
}
