#if UNITY_EDITOR

using UnityEditor;
using UnityEngine;

namespace MixedSpace.Editor
{
    /// <summary>
    /// Inspector 选择器只接受满足所声明 MixedSpace 契约的 Unity 对象。
    /// Inspector picker that stores only Unity objects satisfying a declared MixedSpace
    /// contract.
    /// </summary>
    [CustomPropertyDrawer(typeof(MixedSpaceComponentContractAttribute))]
    internal sealed class MixedSpaceComponentContractDrawer : PropertyDrawer
    {
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var contract = (MixedSpaceComponentContractAttribute)attribute;
            if (property.propertyType != SerializedPropertyType.ObjectReference)
            {
                EditorGUI.HelpBox(
                    position,
                    $"{label.text} must be a Unity object reference for {contract.ContractType.Name}.",
                    MessageType.Error);
                return;
            }

            EditorGUI.BeginProperty(position, label, property);
            var contractLabel = new GUIContent(
                $"{label.text} [{contract.ContractType.Name}]",
                label.tooltip);
            Object candidate = EditorGUI.ObjectField(
                position,
                contractLabel,
                property.objectReferenceValue,
                typeof(Object),
                true);
            Object resolved = Resolve(candidate, contract.ContractType);
            if (candidate == null || resolved != null)
                property.objectReferenceValue = resolved;
            else
                Debug.LogWarning($"'{candidate.name}' does not provide {contract.ContractType.Name}.");
            EditorGUI.EndProperty();
        }

        private static Object Resolve(Object candidate, System.Type contractType)
        {
            if (candidate == null)
                return null;
            if (contractType.IsInstanceOfType(candidate))
                return candidate;

            GameObject owner = candidate as GameObject;
            if (candidate is Component component)
                owner = component.gameObject;
            return owner == null ? null : owner.GetComponent(contractType);
        }
    }
}

#endif
