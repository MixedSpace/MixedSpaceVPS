using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace MixedSpace
{
    /// <summary>Runtime log UI for touch, mouse and existing XR UI input. May also be placed on a custom Canvas.</summary>
    [AddComponentMenu("MixedSpace/Runtime Log Panel")]
    public sealed class MixedSpaceLogPanel : MonoBehaviour
    {
        [SerializeField] private Canvas m_TargetCanvas;
        [SerializeField] private Font m_Font;
        [SerializeField] private bool m_PopupErrors = true;
        [SerializeField, Tooltip("Use a camera-relative world-space canvas for XR. A supplied Canvas keeps its existing render mode and input setup.")]
        private bool m_WorldSpace;
        private static MixedSpaceLogPanel s_Automatic;
        private readonly List<Button> m_Rows = new List<Button>();
        private readonly HashSet<long> m_Announced = new HashSet<long>();
        private readonly HashSet<string> m_AcknowledgedErrors = new HashSet<string>();
        private readonly string[] m_Categories = { "全部", "授权", "定位", "位姿融合", "地图", "运行时", "Unity" };
        private Canvas m_Canvas;
        private GameObject m_OwnedCanvas;
        private GameObject m_Root, m_Panel, m_Popup;
        private Text m_Launcher, m_Details, m_PopupText;
        private RectTransform m_ListContent;

        private Button m_Category, m_Severity, m_Pause;
        private MixedSpaceRuntimeLogEntry[] m_Snapshot = Array.Empty<MixedSpaceRuntimeLogEntry>();
        private MixedSpaceRuntimeLogEntry m_Selected;
        private string m_ActivePopupKey;
        private bool m_Paused;
        private int m_SeenRevision = -1, m_CategoryIndex, m_SeverityIndex;
        private float m_NextRefresh;
        private bool m_OwnsFont;
        private Sprite m_Rounded;
        private Texture2D m_RoundedTexture;
        private Sprite m_Border;
        private Texture2D m_BorderTexture;
        private Text m_Empty;
        private RectTransform m_ListCard, m_DetailCard, m_PopupCard;
        private Text m_Summary, m_PopupHeading;
        private static readonly Color Surface = new Color(0.92f, 0.96f, 1f, 0.90f);
        private static readonly Color Card = new Color(0.98f, 0.995f, 1f, 0.90f);
        private static readonly Color Muted = new Color(0.16f, 0.28f, 0.42f);
        private static readonly Color Accent = new Color(0.08f, 0.44f, 0.86f, 0.96f);
        private static readonly Color Danger = new Color(0.76f, 0.24f, 0.30f);

        internal static void EnsureAvailable()
        {
            if (s_Automatic != null || FindObjectOfType<MixedSpaceLogPanel>(true) != null) return;
            var owner = new GameObject("MixedSpace Runtime Diagnostics");
            DontDestroyOnLoad(owner);
            s_Automatic = owner.AddComponent<MixedSpaceLogPanel>();
        }

        private void Start()
        {
            if (Application.isBatchMode) { enabled = false; return; }
            if (m_Font == null)
            {
                m_Font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft YaHei", "PingFang SC", "Noto Sans CJK SC", "Droid Sans Fallback", "Arial" }, 22);
                m_OwnsFont = true;
            }
            Build();
        }

        private void Update()
        {
            if (m_Root == null) return;
            if (m_OwnedCanvas != null && !m_WorldSpace && UnityEngine.XR.XRSettings.enabled)
            {
                m_WorldSpace = true;
                m_Canvas.renderMode = RenderMode.WorldSpace;
                ((RectTransform)m_Canvas.transform).sizeDelta = new Vector2(1000, 750);
                AddOptionalComponent(m_OwnedCanvas, "UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster");
            }
            if (m_OwnedCanvas != null && m_Canvas.renderMode == RenderMode.ScreenSpaceOverlay && Screen.width > 0 && Screen.height > 0)
            {
                Rect safe = Screen.safeArea;
                var root = (RectTransform)m_Root.transform;
                root.anchorMin = new Vector2(safe.xMin / Screen.width + 0.02f, safe.yMin / Screen.height + 0.02f);
                root.anchorMax = new Vector2(safe.xMax / Screen.width - 0.02f, safe.yMax / Screen.height - 0.02f);
            }
            if (m_OwnedCanvas != null && m_WorldSpace && Camera.main != null && m_Canvas.transform.parent != Camera.main.transform)
            {
                m_Canvas.transform.SetParent(Camera.main.transform, false);
                m_Canvas.transform.localPosition = new Vector3(0, 0, 1.2f);
                m_Canvas.transform.localRotation = Quaternion.identity;
                m_Canvas.transform.localScale = Vector3.one * 0.0012f;
                m_Canvas.worldCamera = Camera.main;
            }
            ApplyLayout();
            if (Time.unscaledTime < m_NextRefresh) return;
            m_NextRefresh = Time.unscaledTime + 0.25f;
            if (m_SeenRevision == MixedSpaceRuntimeLog.Revision) return;
            m_SeenRevision = MixedSpaceRuntimeLog.Revision;
            var latest = MixedSpaceRuntimeLog.Snapshot();
            int errors = 0;
            foreach (var entry in latest)
            {
                if (!entry.IsError) continue;
                errors += entry.Count;
                // While a popup is visible, leave other errors unannounced until it is dismissed.
                if (m_PopupErrors && !m_Popup.activeSelf && !m_Announced.Contains(entry.Id))
                {
                    m_Announced.Add(entry.Id);
                    string key = PopupKey(entry);
                    if (m_AcknowledgedErrors.Contains(key)) continue;
                    m_Selected = entry;
                    m_ActivePopupKey = key;
                    m_PopupHeading.text = entry.Category == "授权" ? "授权验证失败" : "运行出现错误";
                    m_PopupText.text = FormatDetails(entry);
                    m_Popup.SetActive(true);
                }
            }
            m_Launcher.text = errors > 0 ? "●  运行日志   " + errors : "●  运行日志";
            int warnings = 0;
            foreach (var entry in latest) if (entry.Type == LogType.Warning) warnings += entry.Count;
            m_Summary.text = $"{latest.Length} 条记录   /   {errors} 错误   /   {warnings} 警告";
            m_Announced.RemoveWhere(id => Array.FindIndex(latest, entry => entry.Id == id) < 0);
            if (!m_Paused) { m_Snapshot = latest; RefreshRows(); }
        }

        public void Show() { if (m_Panel != null) { m_Panel.SetActive(true); RefreshRows(); } }
        public void Hide() { if (m_Panel != null) m_Panel.SetActive(false); }

        private void Build()
        {
            m_Canvas = m_TargetCanvas;
            if (m_Canvas == null)
            {
                m_WorldSpace |= UnityEngine.XR.XRSettings.enabled;
                m_OwnedCanvas = new GameObject("MixedSpace Log Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
                m_Canvas = m_OwnedCanvas.GetComponent<Canvas>();
                m_Canvas.transform.SetParent(transform, false);
                m_Canvas.renderMode = m_WorldSpace ? RenderMode.WorldSpace : RenderMode.ScreenSpaceOverlay;
                m_Canvas.sortingOrder = 32000;
                var scaler = m_OwnedCanvas.GetComponent<CanvasScaler>();
                scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
                scaler.referenceResolution = new Vector2(1080, 1920);
                scaler.matchWidthOrHeight = 0.5f;
                if (m_WorldSpace)
                {
                    ((RectTransform)m_Canvas.transform).sizeDelta = new Vector2(1000, 750);
                    AddOptionalComponent(m_OwnedCanvas, "UnityEngine.XR.Interaction.Toolkit.UI.TrackedDeviceGraphicRaycaster");
                }
            }
            EnsureInput();
            m_Root = Box("Log UI", m_Canvas.transform, new Vector2(0.02f, 0.04f), new Vector2(0.98f, 0.96f), Color.clear, false).gameObject;
            var launcher = MakeButton("日志", m_Root.transform, new Vector2(0.74f, 0), new Vector2(1, 0.065f), Show);
            launcher.GetComponent<Image>().color = new Color(0.90f, 0.96f, 1f, 0.85f);
            m_Launcher = launcher.GetComponentInChildren<Text>(); m_Launcher.color = new Color(0.08f, 0.32f, 0.54f); m_Launcher.fontSize = 20;
            m_Panel = Box("Log Panel", m_Root.transform, new Vector2(0, 0.09f), Vector2.one, Surface).gameObject;
            AddShadow(m_Panel); Frame((RectTransform)m_Panel.transform, new Color(0.20f, 0.45f, 0.75f, 0.35f));
            Caption("MIXEDSPACE  /  DIAGNOSTICS", m_Panel.transform, V(0.04f, 0.945f), V(0.75f, 0.98f), 14, Muted);
            Caption("运行诊断", m_Panel.transform, V(0.04f, 0.875f), V(0.65f, 0.945f), 34, new Color(0.05f, 0.19f, 0.35f));
            Caption("设备运行状态与错误追踪", m_Panel.transform, V(0.04f, 0.825f), V(0.60f, 0.87f), 18, Muted);
            m_Summary = Caption("等待日志", m_Panel.transform, V(0.56f, 0.825f), V(0.96f, 0.87f), 16, Muted);
            m_Summary.alignment = TextAnchor.MiddleRight;
            Caption("● LIVE", m_Panel.transform, V(0.76f, 0.945f), V(0.90f, 0.98f), 14, Accent);
            var close = MakeButton("×", m_Panel.transform, V(0.91f, 0.885f), V(0.96f, 0.955f), Hide);
            close.GetComponentInChildren<Text>().fontSize = 30;
            m_Category = MakeButton("分类 · 全部", m_Panel.transform, V(0.04f, 0.735f), V(0.32f, 0.80f), () =>
            {
                m_CategoryIndex = (m_CategoryIndex + 1) % m_Categories.Length;
                m_Category.GetComponentInChildren<Text>().text = "分类 · " + m_Categories[m_CategoryIndex];
                FilterStyle(m_Category, m_CategoryIndex != 0); RefreshRows();
            });
            m_Severity = MakeButton("全部级别", m_Panel.transform, V(0.34f, 0.735f), V(0.60f, 0.80f), () =>
            {
                m_SeverityIndex = (m_SeverityIndex + 1) % 3;
                m_Severity.GetComponentInChildren<Text>().text = new[] { "全部级别", "警告和错误", "仅错误" }[m_SeverityIndex];
                FilterStyle(m_Severity, m_SeverityIndex != 0); RefreshRows();
            });
            m_Pause = MakeButton("暂停更新", m_Panel.transform, V(0.62f, 0.735f), V(0.79f, 0.80f), () =>
            {
                m_Paused = !m_Paused; m_Pause.GetComponentInChildren<Text>().text = m_Paused ? "继续" : "暂停"; m_SeenRevision = -1; FilterStyle(m_Pause, m_Paused);
            });
            m_Pause.GetComponentInChildren<Text>().text = "暂停";
            MakeButton("清空", m_Panel.transform, V(0.81f, 0.735f), V(0.96f, 0.80f), () =>
            {
                MixedSpaceRuntimeLog.Clear(); m_Snapshot = Array.Empty<MixedSpaceRuntimeLogEntry>(); m_Selected = null;
                m_AcknowledgedErrors.Clear(); m_Announced.Clear(); m_Popup.SetActive(false);
                m_Summary.text = "0 条记录   /   0 错误   /   0 警告"; m_Launcher.text = "●  运行日志";
                m_SeenRevision = -1; RefreshRows();
            });
            m_ListCard = Box("Event Card", m_Panel.transform, V(0.04f, 0.11f), V(0.60f, 0.70f), Card);
            Frame(m_ListCard, new Color(0.23f, 0.47f, 0.75f, 0.35f));
            Caption("事件记录", m_ListCard, V(0.03f, 0.89f), V(0.65f, 0.99f), 18, Muted);
            Caption("最新优先", m_ListCard, V(0.70f, 0.89f), V(0.97f, 0.99f), 14, Muted).alignment = TextAnchor.MiddleRight;
            Scroll("History", m_ListCard, V(0.025f, 0.025f), V(0.975f, 0.88f), out m_ListContent);
            m_Empty = Caption("暂无日志\n\nSDK 运行后将在这里显示记录", m_ListCard, V(0.08f, 0.25f), V(0.92f, 0.72f), 21, Muted);
            m_Empty.alignment = TextAnchor.MiddleCenter;
            var layout = m_ListContent.gameObject.AddComponent<VerticalLayoutGroup>();
            layout.childControlWidth = true; layout.childForceExpandWidth = true;
            layout.childControlHeight = false; layout.childForceExpandHeight = false; layout.spacing = 8;
            var fit = m_ListContent.gameObject.AddComponent<ContentSizeFitter>(); fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            m_DetailCard = Box("Detail Card", m_Panel.transform, V(0.62f, 0.11f), V(0.96f, 0.70f), Card);
            Frame(m_DetailCard, new Color(0.23f, 0.47f, 0.75f, 0.35f));
            Caption("DETAIL  /  详细信息", m_DetailCard, V(0.04f, 0.89f), V(0.96f, 0.99f), 16, Muted);
            Scroll("Details", m_DetailCard, V(0.045f, 0.04f), V(0.955f, 0.87f), out var detailContent);
            m_Details = FlowingText(detailContent);
            Caption("仅保留最近 200 条 · 凭据字段已脱敏", m_Panel.transform, V(0.04f, 0.025f), V(0.55f, 0.085f), 15, Muted);
            MakeButton("复制详情", m_Panel.transform, V(0.58f, 0.025f), V(0.76f, 0.085f), () => GUIUtility.systemCopyBuffer = m_Selected == null ? string.Empty : FormatDetails(m_Selected));
            var export = MakeButton("复制全部", m_Panel.transform, V(0.78f, 0.025f), V(0.96f, 0.085f), () =>
            {
                var output = new StringBuilder(); foreach (var entry in m_Snapshot) output.AppendLine(FormatDetails(entry));
                GUIUtility.systemCopyBuffer = output.ToString();
            });
            Primary(export);
            m_Popup = Box("Error Popup", m_Root.transform, Vector2.zero, Vector2.one, Color.clear).gameObject;
            m_PopupCard = Box("Error Card", m_Popup.transform, V(0.18f, 0.23f), V(0.82f, 0.80f), new Color(0.95f, 0.98f, 1f, 0.95f)); AddShadow(m_PopupCard.gameObject);
            var icon = Box("Error Icon", m_PopupCard, V(0.05f, 0.77f), V(0.13f, 0.91f), new Color(0.81f, 0.89f, 1f, 0.65f));
            Caption("!", icon, Vector2.zero, Vector2.one, 30, Danger).alignment = TextAnchor.MiddleCenter;
            Caption("需要处理", m_PopupCard, V(0.17f, 0.84f), V(0.94f, 0.92f), 15, Danger);
            m_PopupHeading = Caption("运行出现错误", m_PopupCard, V(0.17f, 0.73f), V(0.94f, 0.85f), 30, new Color(0.05f, 0.19f, 0.35f));
            Caption("完整错误码与调用位置已保留在日志中。", m_PopupCard, V(0.05f, 0.62f), V(0.95f, 0.71f), 17, Muted);
            Scroll("Error Details", m_PopupCard, V(0.05f, 0.24f), V(0.95f, 0.59f), out var errorContent);
            m_PopupText = FlowingText(errorContent);
            var inspect = MakeButton("查看日志", m_PopupCard, V(0.52f, 0.07f), V(0.95f, 0.19f), () => { DismissPopup(); Show(); }); Primary(inspect);
            MakeButton("知道了", m_PopupCard, V(0.05f, 0.07f), V(0.48f, 0.19f), DismissPopup);
            m_Popup.SetActive(false); m_Panel.SetActive(false); ApplyLayout();
        }

        private static Vector2 V(float x, float y) => new Vector2(x, y);

        private void ApplyLayout()
        {
            if (m_Canvas == null || m_ListCard == null) return;
            Rect bounds = ((RectTransform)m_Canvas.transform).rect;
            bool portrait = bounds.height > bounds.width * 1.05f;
            Anchors(m_ListCard, V(0.04f, portrait ? 0.385f : 0.11f), V(portrait ? 0.96f : 0.60f, 0.70f));
            Anchors(m_DetailCard, V(portrait ? 0.04f : 0.62f, 0.11f), V(0.96f, portrait ? 0.365f : 0.70f));
            Anchors(m_PopupCard, V(portrait ? 0.06f : 0.24f, portrait ? 0.30f : 0.24f), V(portrait ? 0.94f : 0.76f, portrait ? 0.70f : 0.78f));
        }

        private static void Anchors(RectTransform rect, Vector2 min, Vector2 max)
        {
            if (rect.anchorMin == min && rect.anchorMax == max) return;
            rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        private Text Caption(string text, Transform parent, Vector2 min, Vector2 max, int size, Color color)
        {
            var label = Label(text, parent, min, max); label.fontSize = size; label.color = color;
            label.alignment = TextAnchor.MiddleLeft; return label;
        }

        private static void Primary(Button button)
        {
            button.GetComponent<Image>().color = Accent;
            button.GetComponentInChildren<Text>().color = Color.white;
        }

        private static void AddShadow(GameObject target)
        {
            var shadow = target.AddComponent<Shadow>(); shadow.effectColor = new Color(0.07f, 0.24f, 0.43f, 0.07f); shadow.effectDistance = V(0, -8);
        }

        private static string PopupKey(MixedSpaceRuntimeLogEntry entry)
        {
            // One structured failure may be reported by authorization, the supervisor and an exception.
            // Acknowledging its stable code dismisses all of those copies, preserving every history entry.
            Match code = Regex.Match(entry.Message, @"\[([A-Z][A-Z0-9_]{2,63})\]");
            return code.Success ? code.Groups[1].Value : entry.Category + "\n" + entry.Message;
        }

        private void DismissPopup()
        {
            if (m_AcknowledgedErrors.Count >= MixedSpaceRuntimeLog.Capacity) m_AcknowledgedErrors.Clear();
            if (m_ActivePopupKey != null) m_AcknowledgedErrors.Add(m_ActivePopupKey);
            m_Popup.SetActive(false); m_SeenRevision = -1;
        }

        private void RefreshRows()
        {
            if (m_Panel == null || !m_Panel.activeSelf) return;
            int row = 0;
            // Newest first, so opening the panel always exposes the latest failure.
            for (int i = m_Snapshot.Length - 1; i >= 0; i--)
            {
                var entry = m_Snapshot[i];
                if (m_CategoryIndex != 0 && entry.Category != m_Categories[m_CategoryIndex]) continue;
                if (m_SeverityIndex == 1 && !entry.IsError && entry.Type != LogType.Warning) continue;
                if (m_SeverityIndex == 2 && !entry.IsError) continue;
                if (row == m_Rows.Count)
                {
                    var button = MakeButton(string.Empty, m_ListContent, Vector2.zero, Vector2.one, null);
                    ((RectTransform)button.transform).sizeDelta = new Vector2(0, 94);
                    var message = button.GetComponentInChildren<Text>(); message.name = "Message";
                    var messageRect = (RectTransform)message.transform; messageRect.anchorMin = V(0.045f, 0.12f); messageRect.anchorMax = V(0.97f, 0.62f);
                    message.alignment = TextAnchor.MiddleLeft; message.fontSize = 20;
                    Caption(string.Empty, button.transform, V(0.045f, 0.64f), V(0.97f, 0.96f), 16, Muted).name = "Metadata";
                    Box("Severity", button.transform, V(0.015f, 0.2f), V(0.022f, 0.8f), Accent, false);
                    m_Rows.Add(button);
                }
                var item = m_Rows[row++]; item.gameObject.SetActive(true); item.onClick.RemoveAllListeners();
                item.onClick.AddListener(() => { m_Selected = entry; RefreshRows(); });
                bool selected = m_Selected != null && m_Selected.Id == entry.Id;
                item.GetComponent<Image>().color = selected ? new Color(0.64f, 0.81f, 1f, 0.90f) : new Color(1f, 1f, 1f, 0.84f);
                var label = item.transform.Find("Message").GetComponent<Text>();
                string line = Regex.Replace(entry.Message, @"^(?:\[[^\]\r\n]+\]\s*)+", string.Empty).Replace('\n', ' ').Replace('\r', ' ');
                if (line.Length > 110) line = line.Substring(0, 110) + "…";
                label.text = line; label.color = new Color(0.07f, 0.20f, 0.36f);
                var severity = entry.IsError ? Danger : entry.Type == LogType.Warning ? new Color(0.70f, 0.43f, 0.13f) : Accent;
                item.transform.Find("Severity").GetComponent<Image>().color = severity;
                var metadata = item.transform.Find("Metadata").GetComponent<Text>();
                metadata.text = $"{entry.Category}  ·  {(entry.IsError ? "错误" : entry.Type == LogType.Warning ? "警告" : "信息")}     {entry.Time:HH:mm:ss}" + (entry.Count > 1 ? $"   ×{entry.Count}" : string.Empty);
                metadata.color = entry.IsError ? Danger : Muted;
            }
            for (int i = row; i < m_Rows.Count; i++) m_Rows[i].gameObject.SetActive(false);
            m_Empty.gameObject.SetActive(row == 0);
            m_Empty.text = m_Snapshot.Length == 0 ? "暂无日志\n\nSDK 运行后将在这里显示记录" : "没有符合筛选条件的记录";
            m_Details.text = m_Selected == null ? "选择一条事件\n\n在此查看完整信息、错误码和调用堆栈。" : FormatDetails(m_Selected);
        }

        private static string FormatDetails(MixedSpaceRuntimeLogEntry entry) =>
            $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{entry.Category}] {entry.Type} ×{entry.Count}\n{entry.Message}\n{entry.StackTrace}";

        private RectTransform Box(string name, Transform parent, Vector2 min, Vector2 max, Color color, bool raycast = true)
        {
            var obj = new GameObject(name, typeof(RectTransform), typeof(Image)); obj.transform.SetParent(parent, false);
            var rect = (RectTransform)obj.transform; rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = rect.offsetMax = Vector2.zero;
            var image = obj.GetComponent<Image>(); image.sprite = RoundedSprite(); image.type = Image.Type.Sliced;
            image.color = color; image.raycastTarget = raycast; return rect;
        }

        private Text Label(string value, Transform parent, Vector2 min, Vector2 max)
        {
            var obj = new GameObject("Text", typeof(RectTransform), typeof(Text)); obj.transform.SetParent(parent, false);
            var rect = (RectTransform)obj.transform; rect.anchorMin = min; rect.anchorMax = max; rect.offsetMin = new Vector2(8, 3); rect.offsetMax = new Vector2(-8, -3);
            var text = obj.GetComponent<Text>(); text.font = m_Font; text.fontSize = 19; text.color = new Color(0.07f, 0.20f, 0.36f);
            text.supportRichText = false; text.raycastTarget = false; text.alignment = TextAnchor.MiddleCenter; text.text = value; return text;
        }

        private Button MakeButton(string caption, Transform parent, Vector2 min, Vector2 max, Action action)
        {
            var rect = Box(caption, parent, min, max, new Color(0.72f, 0.84f, 0.97f, 0.90f));
            var button = rect.gameObject.AddComponent<Button>(); button.targetGraphic = rect.GetComponent<Image>();
            Frame(rect, new Color(0.18f, 0.42f, 0.72f, 0.50f));
            Label(caption, rect, Vector2.zero, Vector2.one);
            if (action != null) button.onClick.AddListener(() => action()); return button;
        }

        private ScrollRect Scroll(string name, Transform parent, Vector2 min, Vector2 max, out RectTransform content)
        {
            var viewport = Box(name, parent, min, max, new Color(0.88f, 0.94f, 1f, 0.16f)); viewport.gameObject.AddComponent<RectMask2D>();
            content = new GameObject("Content", typeof(RectTransform)).GetComponent<RectTransform>(); content.SetParent(viewport, false);
            content.anchorMin = new Vector2(0, 1); content.anchorMax = Vector2.one; content.pivot = new Vector2(0.5f, 1); content.sizeDelta = Vector2.zero;
            var scroll = viewport.gameObject.AddComponent<ScrollRect>(); scroll.viewport = viewport; scroll.content = content;
            scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped; return scroll;
        }

        private Text FlowingText(RectTransform content)
        {
            // Text itself is the content so preferred height follows wrapping and long stack traces scroll.
            var text = content.gameObject.AddComponent<Text>(); text.font = m_Font; text.fontSize = 20; text.color = new Color(0.12f, 0.28f, 0.45f);
            text.supportRichText = false; text.alignment = TextAnchor.UpperLeft; text.raycastTarget = false;
            var fit = content.gameObject.AddComponent<ContentSizeFitter>(); fit.verticalFit = ContentSizeFitter.FitMode.PreferredSize; return text;
        }

        private Sprite RoundedSprite()
        {
            if (m_Rounded != null) return m_Rounded;
            const int size = 64;
            const float radius = 18;
            m_RoundedTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            m_RoundedTexture.wrapMode = TextureWrapMode.Clamp; m_RoundedTexture.filterMode = FilterMode.Bilinear;
            var colors = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Max(radius - (x + 0.5f), x + 0.5f - (size - radius));
                    float dy = Mathf.Max(radius - (y + 0.5f), y + 0.5f - (size - radius));
                    float distance = Mathf.Sqrt(Mathf.Max(dx, 0) * Mathf.Max(dx, 0) + Mathf.Max(dy, 0) * Mathf.Max(dy, 0));
                    colors[y * size + x] = new Color(1, 1, 1, Mathf.Clamp01(radius - distance + 0.5f));
                }
            m_RoundedTexture.SetPixels(colors); m_RoundedTexture.Apply();
            m_Rounded = Sprite.Create(m_RoundedTexture, new Rect(0, 0, size, size), V(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, new Vector4(20, 20, 20, 20));
            return m_Rounded;
        }

        private static void FilterStyle(Button button, bool active)
        {
            button.GetComponent<Image>().color = active ? Accent : new Color(0.72f, 0.84f, 0.97f, 0.90f);
            button.GetComponentInChildren<Text>().color = active ? Color.white : new Color(0.07f, 0.20f, 0.36f);
        }

        private void Frame(RectTransform parent, Color color)
        {
            if (m_Border == null)
            {
                const int size = 64; const float radius = 18;
                m_BorderTexture = new Texture2D(size, size, TextureFormat.RGBA32, false);
                m_BorderTexture.wrapMode = TextureWrapMode.Clamp; m_BorderTexture.filterMode = FilterMode.Bilinear;
                var pixels = new Color[size * size];
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                    {
                        float dx = Mathf.Abs(x + 0.5f - size / 2f) - (size / 2f - radius);
                        float dy = Mathf.Abs(y + 0.5f - size / 2f) - (size / 2f - radius);
                        float distance = Mathf.Sqrt(Mathf.Max(dx, 0) * Mathf.Max(dx, 0) + Mathf.Max(dy, 0) * Mathf.Max(dy, 0)) + Mathf.Min(Mathf.Max(dx, dy), 0) - radius;
                        pixels[y * size + x] = new Color(1, 1, 1, Mathf.Clamp01(1.25f - Mathf.Abs(distance + 0.75f)));
                    }
                m_BorderTexture.SetPixels(pixels); m_BorderTexture.Apply();
                m_Border = Sprite.Create(m_BorderTexture, new Rect(0, 0, size, size), V(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, new Vector4(20, 20, 20, 20));
            }
            var obj = new GameObject("Border", typeof(RectTransform), typeof(Image)); obj.transform.SetParent(parent, false);
            var rect = (RectTransform)obj.transform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
            var image = obj.GetComponent<Image>(); image.sprite = m_Border; image.type = Image.Type.Sliced; image.color = color; image.raycastTarget = false;
        }

        private static Component AddOptionalComponent(GameObject target, string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null && typeof(Component).IsAssignableFrom(type)) return target.AddComponent(type);
            }
            return null;
        }

        private static void EnsureInput()
        {
            if (FindObjectOfType<EventSystem>() != null) return;
            var obj = new GameObject("MixedSpace Log EventSystem", typeof(EventSystem)); DontDestroyOnLoad(obj);
            if (UnityEngine.XR.XRSettings.enabled &&
                AddOptionalComponent(obj, "UnityEngine.XR.Interaction.Toolkit.UI.XRUIInputModule") != null) return;
#if ENABLE_INPUT_SYSTEM && !ENABLE_LEGACY_INPUT_MANAGER
            var module = AddOptionalComponent(obj, "UnityEngine.InputSystem.UI.InputSystemUIInputModule");
            module?.GetType().GetMethod("AssignDefaultActions")?.Invoke(module, null);
#else
            obj.AddComponent<StandaloneInputModule>();
#endif
        }

        private void OnEnable() { if (m_Root != null) m_Root.SetActive(true); }
        private void OnDisable() { if (m_Root != null) m_Root.SetActive(false); }
        private void OnDestroy()
        {
            if (m_OwnedCanvas != null) Destroy(m_OwnedCanvas);
            else if (m_Root != null) Destroy(m_Root);
            if (m_OwnsFont && m_Font != null) Destroy(m_Font);
            if (m_Rounded != null) Destroy(m_Rounded);
            if (m_RoundedTexture != null) Destroy(m_RoundedTexture);
            if (m_Border != null) Destroy(m_Border);
            if (m_BorderTexture != null) Destroy(m_BorderTexture);
            if (s_Automatic == this) s_Automatic = null;
        }
    }
}
