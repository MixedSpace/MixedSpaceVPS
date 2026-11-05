#if UNITY_EDITOR

using System.IO;
using System.Text;
using System.Xml;

namespace MixedSpace.EditorTools
{
    /// <summary>
    /// 用于修复环境的清单访问边界，统一处理 XML 命名空间，项目规则无需了解文档遍历细节。
    /// Narrow manifest gateway used by readiness repairs. It owns XML namespace
    /// handling so project rules do not depend on document traversal details.
    /// </summary>
    internal sealed class MixedSpaceAndroidManifestDocument
    {
        private const string AndroidNamespace = "http://schemas.android.com/apk/res/android";
        private readonly string m_Path;
        private readonly XmlDocument m_Document = new XmlDocument();
        private readonly XmlNamespaceManager m_Namespaces;

        public MixedSpaceAndroidManifestDocument(string path)
        {
            m_Path = path;
            using (var reader = new XmlTextReader(path))
            {
                reader.Read();
                m_Document.Load(reader);
            }

            m_Namespaces = new XmlNamespaceManager(m_Document.NameTable);
            m_Namespaces.AddNamespace("android", AndroidNamespace);
        }

        public bool ContainsPermission(string permission)
        {
            string escaped = permission.Replace("'", "&apos;");
            return m_Document.SelectSingleNode(
                $"/manifest/uses-permission[@android:name='{escaped}']",
                m_Namespaces) != null;
        }

        public void EnsurePermission(string permission)
        {
            if (ContainsPermission(permission))
                return;

            XmlElement root = m_Document.DocumentElement;
            if (root == null)
                throw new InvalidDataException("Android manifest has no root element.");

            XmlElement element = m_Document.CreateElement("uses-permission");
            XmlAttribute name = m_Document.CreateAttribute("android", "name", AndroidNamespace);
            name.Value = permission;
            element.Attributes.Append(name);
            root.AppendChild(element);
        }

        public void Save()
        {
            using (var writer = new XmlTextWriter(m_Path, new UTF8Encoding(false)))
            {
                writer.Formatting = Formatting.Indented;
                m_Document.Save(writer);
            }
        }
    }
}

#endif
