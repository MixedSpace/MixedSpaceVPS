#if UNITY_EDITOR
using UnityEditor;
using MixedSpace.XR;

namespace MixedSpace.EditorTools
{
    [CustomEditor(typeof(MixedSpaceWorldRoot))]
    public sealed class MixedSpaceWorldRootEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.LabelField("MIXEDSPACE WORLD", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Place Map Bindings and world-locked content below this coordinate root. History is owned by the World runtime, not by components.", MessageType.Info);
        }
    }
}
#endif
