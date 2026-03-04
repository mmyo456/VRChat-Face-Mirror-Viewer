using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UIElements;

using Klak.Spout;
using UniRx;

// 主界面控制脚本：负责 Spout 画面显示、背景设置、窗口行为与运行参数。
public class ViewerUI : MonoBehaviour
{
    // UI 初始化状态，避免重复绑定事件。
    private bool initialized;
    // 记录当前绑定的根节点，处理 UIDocument 重建场景。
    private VisualElement boundRoot;
    // 自定义背景纹理缓存，便于复用与释放。
    private Texture2D customBackgroundTexture;
    // 运行期订阅统一管理，避免生命周期泄漏。
    private readonly CompositeDisposable runtimeSubscriptions = new CompositeDisposable();
    private const string SettingsFileName = "viewer-settings.json";
    // 默认刷新率为 60，允许用户手动调到 240。
    private const int DefaultTargetFrameRate = 60;
    private const int MaxTargetFrameRate = 240;
    private const int MinTargetFrameRate = 1;
    // 控制面板逻辑更新频率（不是渲染帧率）。
    private const float ControlPanelUpdateInterval = 1f / 60f;
    // 当前运行时刷新率（由 UI / 配置驱动）。
    private int runtimeTargetFrameRate = DefaultTargetFrameRate;

    [Serializable]
    private class ViewerSettings
    {
        // 选中的 Spout 源名称。
        public string spoutSourceName = string.Empty;
        // 背景模式（默认/纯色/自定义）。
        public string backgroundMode = "默认";
        // 自定义背景路径。
        public string backgroundPath = string.Empty;
        // 是否缩放到屏幕。
        public bool scaleToScreen = true;
        // 是否镜像画面。
        public bool mirror;
        // 是否全屏。
        public bool fullScreen;
        // 是否置顶窗口。
        public bool topMost;
        // 刷新率设置（1~240）。
        public int targetFrameRate = DefaultTargetFrameRate;
    }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern System.IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    private static extern System.IntPtr MonitorFromWindow(System.IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(System.IntPtr hMonitor, ref MONITORINFO lpmi);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("comdlg32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileName([In, Out] OpenFileName ofn);

    private const int OfnExplorer = 0x00080000;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnHideReadOnly = 0x00000004;
    private const int OfnNoChangeDir = 0x00000008;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class OpenFileName
    {
        public int structSize = Marshal.SizeOf(typeof(OpenFileName));
        public IntPtr dlgOwner = IntPtr.Zero;
        public IntPtr instance = IntPtr.Zero;
        public string filter = null;
        public string customFilter = null;
        public int maxCustFilter = 0;
        public int filterIndex = 1;
        public string file = new string('\0', 1024);
        public int maxFile = 1024;
        public string fileTitle = new string('\0', 256);
        public int maxFileTitle = 256;
        public string initialDir = null;
        public string title = null;
        public int flags = 0;
        public short fileOffset = 0;
        public short fileExtension = 0;
        public string defExt = null;
        public IntPtr custData = IntPtr.Zero;
        public IntPtr hook = IntPtr.Zero;
        public string templateName = null;
        public IntPtr reservedPtr = IntPtr.Zero;
        public int reservedInt = 0;
        public int flagsEx = 0;
    }
#endif

    private void OnDestroy()
    {
        // 组件销毁时释放所有订阅与运行时纹理资源。
        runtimeSubscriptions.Dispose();
        if (customBackgroundTexture != null)
        {
            Destroy(customBackgroundTexture);
            customBackgroundTexture = null;
        }
    }

    private void OnEnable()
    {
        // 保持后台运行，方便多开与副屏场景。
        Application.runInBackground = true;

        const float controlPanelMaxOpacity = 0.7f;
        const float controlPanelIdleDelay = 3f;
        const float controlPanelFadeDuration = 1.5f;

        var alwaysOnTop = GetComponent<AlwaysOnTop>();
        var doc = GetComponent<UIDocument>();
        if (doc == null)
        {
            return;
        }

        var root = doc.rootVisualElement;
        if (root == null)
        {
            return;
        }

        if (initialized && ReferenceEquals(boundRoot, root))
        {
            return;
        }

        runtimeSubscriptions.Clear();
        boundRoot = root;

        var bg = root.Q("BG");
        var scaled = root.Q("Scaled");
        var unscaled = root.Q("Unscaled");
        var unscaledContent = unscaled?.parent;

        var customScaledLayer = bg?.Q<VisualElement>("CustomBgScaled");
        if (bg != null && customScaledLayer == null)
        {
            customScaledLayer = new VisualElement { name = "CustomBgScaled" };
            customScaledLayer.style.position = Position.Absolute;
            customScaledLayer.style.left = 0f;
            customScaledLayer.style.top = 0f;
            customScaledLayer.style.right = 0f;
            customScaledLayer.style.bottom = 0f;
            customScaledLayer.style.display = DisplayStyle.None;
            customScaledLayer.style.backgroundColor = Color.clear;
            SetBackgroundScaleMode(customScaledLayer, ScaleMode.ScaleToFit);
            customScaledLayer.pickingMode = PickingMode.Ignore;
            bg.Insert(0, customScaledLayer);
        }

        var customUnscaledLayer = unscaledContent?.Q<VisualElement>("CustomBgUnscaled");
        if (unscaledContent != null && customUnscaledLayer == null)
        {
            customUnscaledLayer = new VisualElement { name = "CustomBgUnscaled" };
            customUnscaledLayer.style.position = Position.Absolute;
            customUnscaledLayer.style.left = 0f;
            customUnscaledLayer.style.top = 0f;
            customUnscaledLayer.style.display = DisplayStyle.None;
            customUnscaledLayer.style.backgroundColor = Color.clear;
            SetBackgroundScaleMode(customUnscaledLayer, ScaleMode.ScaleToFit);
            customUnscaledLayer.pickingMode = PickingMode.Ignore;
            unscaledContent.Insert(0, customUnscaledLayer);
        }
        // 绑定 UI 控件。
        var controlPanel = root.Q<VisualElement>("ControlPanel");
        var spoutDropdown = root.Q<DropdownField>("SpoutNames");
        var backgroundModeDropdown = root.Q<DropdownField>("BgMode");
        var backgroundPathField = root.Q<TextField>("BgPath");
        var scrollView = root.Q<ScrollView>("UnscaledScroll");
        var topMost = root.Q<Toggle>("TopMost");
        var scaleToggle = root.Q<Toggle>("TexScale");
        var mirrorToggle = root.Q<Toggle>("Mirror");
        var fullScreenToggle = root.Q<Toggle>("FullScreen");
        var targetFrameRateField = root.Q<IntegerField>("TargetFrameRate");
        var saveTextureButton = root.Q<Button>("SaveTexture");
        var saveSettingsButton = root.Q<Button>("SaveSettings");

        var spoutReceiver = GetComponent<SpoutReceiver>();
        if (spoutReceiver == null)
        {
            return;
        }

        var currentTex = (RenderTexture)null;
        var lastControlPanelActivityTime = Time.unscaledTime;
        var isSourceDropdownInteracting = false;
        var lastSourceDropdownInteractionTime = Time.unscaledTime;
        const float dropdownPopupScanInterval = 0.15f;
        var cachedDropdownPopupVisible = false;
        var nextDropdownPopupScanTime = 0f;
        var nextUiUpdateTime = 0f;
        const float fullscreenProbeInterval = 0.5f;
        var cachedFullscreenState = IsFullscreenActive();
        var nextFullscreenProbeTime = 0f;
        var lastBackgroundPickerOpenTime = -10f;
        var windowedWidth = Screen.width;
        var windowedHeight = Screen.height;
        var lastScreenWidth = Screen.width;
        var lastScreenHeight = Screen.height;
        // 读取并应用持久化设置。
        var loadedSettings = LoadSettings();
        runtimeTargetFrameRate = SanitizeTargetFrameRate(loadedSettings != null ? loadedSettings.targetFrameRate : runtimeTargetFrameRate);
        ApplyRuntimePerformanceMode();

        if (scaled != null)
        {
            scaled.style.backgroundColor = Color.clear;
        }
        if (scrollView != null)
        {
            scrollView.style.backgroundColor = Color.clear;
        }

        // Spout 源下拉框绑定。
        if (spoutDropdown != null)
        {
            spoutDropdown.style.display = DisplayStyle.Flex;
            spoutDropdown.choices = SpoutManager.GetSourceNames().ToList();
            if (loadedSettings != null && !string.IsNullOrWhiteSpace(loadedSettings.spoutSourceName) && spoutDropdown.choices.Contains(loadedSettings.spoutSourceName))
            {
                spoutDropdown.SetValueWithoutNotify(loadedSettings.spoutSourceName);
            }
            else if (spoutDropdown.choices.Count > 0 && string.IsNullOrWhiteSpace(spoutDropdown.value))
            {
                spoutDropdown.SetValueWithoutNotify(spoutDropdown.choices[0]);
            }
            if (!string.IsNullOrWhiteSpace(spoutDropdown.value))
            {
                spoutReceiver.sourceName = spoutDropdown.value;
            }
            spoutDropdown.RegisterValueChangedCallback(evt => spoutReceiver.sourceName = evt.newValue);
            spoutDropdown.RegisterCallback<FocusInEvent>(_ =>
            {
                isSourceDropdownInteracting = true;
                lastSourceDropdownInteractionTime = Time.unscaledTime;
                cachedDropdownPopupVisible = true;
                SetTexture(spoutReceiver.receivedTexture, spoutDropdown.value);
                spoutDropdown.choices = SpoutManager.GetSourceNames().ToList();
                MarkControlPanelActive();
            });
            spoutDropdown.RegisterCallback<PointerDownEvent>(_ =>
            {
                isSourceDropdownInteracting = true;
                lastSourceDropdownInteractionTime = Time.unscaledTime;
                cachedDropdownPopupVisible = true;
                MarkControlPanelActive();
            });
            spoutDropdown.RegisterCallback<FocusOutEvent>(_ =>
            {
                cachedDropdownPopupVisible = false;
                lastSourceDropdownInteractionTime = Time.unscaledTime;
            });
            spoutDropdown.RegisterValueChangedCallback(_ =>
            {
                isSourceDropdownInteracting = false;
                cachedDropdownPopupVisible = false;
                lastSourceDropdownInteractionTime = Time.unscaledTime;
            });
        }

        // 背景模式与背景路径绑定。
        if (backgroundModeDropdown != null)
        {
            backgroundModeDropdown.choices = new[] { "默认", "红色", "蓝色", "绿色", "洋红色", "灰色", "白色", "自定义" }.ToList();
            if (!backgroundModeDropdown.choices.Contains(backgroundModeDropdown.value))
            {
                backgroundModeDropdown.SetValueWithoutNotify("默认");
            }
            if (loadedSettings != null && !string.IsNullOrWhiteSpace(loadedSettings.backgroundMode) && backgroundModeDropdown.choices.Contains(loadedSettings.backgroundMode))
            {
                backgroundModeDropdown.SetValueWithoutNotify(loadedSettings.backgroundMode);
            }

            backgroundModeDropdown.RegisterValueChangedCallback(_ =>
            {
                UpdateBackgroundControlsVisibility();
                ApplyBackgroundFromSelection();
                MarkControlPanelActive();
            });
        }
        if (backgroundPathField != null && loadedSettings != null)
        {
            backgroundPathField.SetValueWithoutNotify(loadedSettings.backgroundPath ?? string.Empty);
        }


        backgroundPathField?.RegisterValueChangedCallback(_ =>
        {
            if (IsCustomBackgroundMode())
            {
                ApplyBackgroundFromSelection();
            }
            MarkControlPanelActive();
        });

        backgroundPathField?.RegisterCallback<PointerDownEvent>(evt =>
        {
            if (evt.button == (int)MouseButton.RightMouse)
            {
                return;
            }

            TryOpenBackgroundPicker();
        }, TrickleDown.TrickleDown);

        backgroundPathField?.RegisterCallback<ClickEvent>(_ =>
        {
            TryOpenBackgroundPicker();
        }, TrickleDown.TrickleDown);

        UpdateBackgroundControlsVisibility();
        ApplyBackgroundFromSelection();

        if (topMost != null && loadedSettings != null)
        {
            topMost.SetValueWithoutNotify(loadedSettings.topMost);
        }

        topMost?.RegisterValueChangedCallback(evt =>
        {
            if (alwaysOnTop != null)
            {
                alwaysOnTop.AssignTopmostWindow(evt.newValue);
            }
            MarkControlPanelActive();
        });

        if (alwaysOnTop != null && topMost != null)
        {
            alwaysOnTop.AssignTopmostWindow(topMost.value);
        }

        // 刷新率输入：即时生效并限制在 1~240。
        if (targetFrameRateField != null)
        {
            targetFrameRateField.SetValueWithoutNotify(runtimeTargetFrameRate);
            targetFrameRateField.RegisterValueChangedCallback(evt =>
            {
                var sanitized = SanitizeTargetFrameRate(evt.newValue);
                if (sanitized != evt.newValue)
                {
                    targetFrameRateField.SetValueWithoutNotify(sanitized);
                }

                runtimeTargetFrameRate = sanitized;
                ApplyRuntimePerformanceMode();
                MarkControlPanelActive();
            });
        }

        scaleToggle?.RegisterValueChangedCallback(evt =>
        {
            if (scrollView != null)
            {
                scrollView.style.display = evt.newValue ? DisplayStyle.None : DisplayStyle.Flex;
            }
            if (scaled != null)
            {
                scaled.style.display = evt.newValue ? DisplayStyle.Flex : DisplayStyle.None;
            }
            UpdateCustomBackgroundScaleMode();
            MarkControlPanelActive();
        });

        var initialScaleToScreen = loadedSettings != null ? loadedSettings.scaleToScreen : true;
        if (scaleToggle != null)
        {
            scaleToggle.SetValueWithoutNotify(initialScaleToScreen);
        }
        if (scrollView != null)
        {
            scrollView.style.display = initialScaleToScreen ? DisplayStyle.None : DisplayStyle.Flex;
        }
        if (scaled != null)
        {
            scaled.style.display = initialScaleToScreen ? DisplayStyle.Flex : DisplayStyle.None;
        }
        UpdateCustomBackgroundScaleMode();

        if (mirrorToggle != null)
        {
            if (loadedSettings != null)
            {
                mirrorToggle.SetValueWithoutNotify(loadedSettings.mirror);
            }
            mirrorToggle.RegisterValueChangedCallback(evt =>
            {
                SetMirror(evt.newValue);
                MarkControlPanelActive();
            });
            SetMirror(mirrorToggle.value);
        }

        if (fullScreenToggle != null)
        {
            var initialFullscreen = loadedSettings != null ? loadedSettings.fullScreen : cachedFullscreenState;
            fullScreenToggle.SetValueWithoutNotify(initialFullscreen);
            fullScreenToggle.RegisterValueChangedCallback(evt =>
            {
                SetFullScreen(evt.newValue);
                cachedFullscreenState = evt.newValue;
                nextFullscreenProbeTime = 0f;
                MarkControlPanelActive();
            });
            if (initialFullscreen != cachedFullscreenState)
            {
                SetFullScreen(initialFullscreen);
                cachedFullscreenState = initialFullscreen;
            }
        }

        if (saveTextureButton != null)
        {
            saveTextureButton.clicked += () =>
            {
                SaveTexture();
                MarkControlPanelActive();
            };
        }

        // 保存设置按钮：把当前 UI 状态写入配置文件。
        if (saveSettingsButton != null)
        {
            saveSettingsButton.clicked += () =>
            {
                SaveSettings(new ViewerSettings
                {
                    spoutSourceName = spoutDropdown != null ? spoutDropdown.value : string.Empty,
                    backgroundMode = backgroundModeDropdown != null ? backgroundModeDropdown.value : "默认",
                    backgroundPath = backgroundPathField != null ? backgroundPathField.value : string.Empty,
                    scaleToScreen = scaleToggle != null && scaleToggle.value,
                    mirror = mirrorToggle != null && mirrorToggle.value,
                    fullScreen = fullScreenToggle != null ? fullScreenToggle.value : IsFullscreenActive(),
                    topMost = topMost != null && topMost.value,
                    targetFrameRate = runtimeTargetFrameRate
                });
                MarkControlPanelActive();
            };
        }

        controlPanel?.RegisterCallback<PointerDownEvent>(_ => MarkControlPanelActive());
        root.RegisterCallback<GeometryChangedEvent>(_ => { UpdateControlPanelLayout(); UpdateCustomBackgroundScaleMode(); });

        HideControlPanelImmediately();
        UpdateControlPanelLayout();

        // 监听 Spout 纹理变化并刷新显示。
        spoutReceiver.ObserveEveryValueChanged(r => r.receivedTexture)
            .Subscribe(tex => SetTexture(tex, spoutDropdown != null ? spoutDropdown.value : string.Empty))
            .AddTo(runtimeSubscriptions);

        // 每帧更新控制面板逻辑（含节流）。
        Observable.EveryUpdate()
            .Where(_ => isActiveAndEnabled)
            .Subscribe(_ =>
            {
                if (Screen.width != lastScreenWidth || Screen.height != lastScreenHeight)
                {
                    lastScreenWidth = Screen.width;
                    lastScreenHeight = Screen.height;
                    UpdateControlPanelLayout();
                    UpdateCustomBackgroundScaleMode();
                }

                if (Time.unscaledTime < nextUiUpdateTime)
                {
                    return;
                }

                nextUiUpdateTime = Time.unscaledTime + ControlPanelUpdateInterval;
                UpdateControlPanel();
            })
            .AddTo(runtimeSubscriptions);

        initialized = true;

        // 当前是否处于“自定义背景”模式。
        bool IsCustomBackgroundMode()
        {
            return backgroundModeDropdown != null && backgroundModeDropdown.value == "自定义";
        }

        // 尝试打开背景图片选择器（含防抖）。
        void TryOpenBackgroundPicker()
        {
            if (backgroundPathField == null || !IsCustomBackgroundMode())
            {
                return;
            }

            if (Time.unscaledTime - lastBackgroundPickerOpenTime < 0.25f)
            {
                return;
            }

            lastBackgroundPickerOpenTime = Time.unscaledTime;
            var selectedPath = OpenImageFileDialog();
            if (!string.IsNullOrWhiteSpace(selectedPath))
            {
                backgroundPathField.SetValueWithoutNotify(selectedPath);
                ApplyBackgroundFromSelection();
            }

            MarkControlPanelActive();
        }

        // 根据“缩放到屏幕”开关选择背景缩放模式。
        ScaleMode GetCustomBackgroundScaleMode()
        {
            return scaleToggle != null && scaleToggle.value
                ? ScaleMode.ScaleToFit
                : ScaleMode.ScaleAndCrop;
        }

        // 更新自定义背景在缩放/非缩放场景下的展示方式。
        void UpdateCustomBackgroundScaleMode()
        {
            if (customBackgroundTexture == null || !IsCustomBackgroundMode())
            {
                if (customScaledLayer != null)
                {
                    customScaledLayer.style.display = DisplayStyle.None;
                }
                if (customUnscaledLayer != null)
                {
                    customUnscaledLayer.style.display = DisplayStyle.None;
                }
                return;
            }

            var scaleToScreen = scaleToggle != null && scaleToggle.value;
            if (scaleToScreen)
            {
                if (customScaledLayer != null)
                {
                    customScaledLayer.style.display = DisplayStyle.Flex;
                    customScaledLayer.style.left = 0f;
                    customScaledLayer.style.top = 0f;
                    customScaledLayer.style.right = 0f;
                    customScaledLayer.style.bottom = 0f;
                    customScaledLayer.style.width = StyleKeyword.Auto;
                    customScaledLayer.style.height = StyleKeyword.Auto;
                    SetBackgroundScaleMode(customScaledLayer, ScaleMode.ScaleToFit);
                }

                if (customUnscaledLayer != null)
                {
                    customUnscaledLayer.style.display = DisplayStyle.None;
                }

                return;
            }

            if (customScaledLayer != null)
            {
                customScaledLayer.style.display = DisplayStyle.None;
            }

            if (customUnscaledLayer != null)
            {
                var targetWidth = customBackgroundTexture.width;
                var targetHeight = customBackgroundTexture.height;
                if (currentTex != null)
                {
                    targetWidth = currentTex.width;
                    targetHeight = currentTex.height;
                }

                customUnscaledLayer.style.width = targetWidth;
                customUnscaledLayer.style.height = targetHeight;
                SetBackgroundScaleMode(customUnscaledLayer, ScaleMode.ScaleToFit);
                customUnscaledLayer.style.display = DisplayStyle.Flex;
            }
        }

        // 使用 UI Toolkit 新的 background-* 属性替代已弃用的 unityBackgroundScaleMode。
        void SetBackgroundScaleMode(VisualElement element, ScaleMode mode)
        {
            if (element == null)
            {
                return;
            }

            BackgroundSize backgroundSize;
            switch (mode)
            {
                case ScaleMode.ScaleAndCrop:
                    backgroundSize = new BackgroundSize(BackgroundSizeType.Cover);
                    break;
                case ScaleMode.StretchToFill:
                    backgroundSize = new BackgroundSize(
                        new Length(100f, LengthUnit.Percent),
                        new Length(100f, LengthUnit.Percent)
                    );
                    break;
                default:
                    backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                    break;
            }

            element.style.backgroundSize = new StyleBackgroundSize(backgroundSize);
            element.style.backgroundPositionX = new StyleBackgroundPosition(new BackgroundPosition(BackgroundPositionKeyword.Center));
            element.style.backgroundPositionY = new StyleBackgroundPosition(new BackgroundPosition(BackgroundPositionKeyword.Center));
            element.style.backgroundRepeat = new StyleBackgroundRepeat(new BackgroundRepeat(Repeat.NoRepeat, Repeat.NoRepeat));
        }

        // 根据背景模式控制背景路径输入框显隐。
        void UpdateBackgroundControlsVisibility()
        {
            var showCustom = IsCustomBackgroundMode();
            if (backgroundPathField != null)
            {
                backgroundPathField.style.display = showCustom ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

        // 按当前下拉选择应用背景。
        void ApplyBackgroundFromSelection()
        {
            var mode = backgroundModeDropdown != null ? backgroundModeDropdown.value : "默认";
            switch (mode)
            {
                case "红色":
                    ApplyBackgroundColor(new Color(0.75f, 0.1f, 0.1f, 1f));
                    break;
                case "蓝色":
                    ApplyBackgroundColor(new Color(0.1f, 0.25f, 0.75f, 1f));
                    break;
                case "绿色":
                    ApplyBackgroundColor(new Color(0.1f, 0.6f, 0.2f, 1f));
                    break;
                case "洋红色":
                    ApplyBackgroundColor(new Color(0.8f, 0.15f, 0.8f, 1f));
                    break;
                case "灰色":
                    ApplyBackgroundColor(new Color(0.25f, 0.25f, 0.25f, 1f));
                    break;
                case "白色":
                    ApplyBackgroundColor(Color.white);
                    break;
                case "自定义":
                    ApplyCustomBackground(backgroundPathField != null ? backgroundPathField.value : string.Empty);
                    break;
                default:
                    ApplyBackgroundColor(Color.black);
                    break;
            }
        }

        // 应用纯色背景，并清理自定义背景图层。
        void ApplyBackgroundColor(Color color)
        {
            if (bg == null)
            {
                return;
            }

            if (customScaledLayer != null)
            {
                customScaledLayer.style.display = DisplayStyle.None;
                customScaledLayer.style.backgroundImage = StyleKeyword.None;
            }
            if (customUnscaledLayer != null)
            {
                customUnscaledLayer.style.display = DisplayStyle.None;
                customUnscaledLayer.style.backgroundImage = StyleKeyword.None;
            }

            bg.style.backgroundImage = StyleKeyword.None;
            bg.style.backgroundColor = color;
        }

        // 从本地文件加载并应用自定义背景。
        void ApplyCustomBackground(string path)
        {
            if (bg == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ApplyBackgroundColor(Color.black);
                return;
            }

            try
            {
                var bytes = File.ReadAllBytes(path);
                var loaded = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!loaded.LoadImage(bytes))
                {
                    Destroy(loaded);
                    ApplyBackgroundColor(Color.black);
                    return;
                }

                if (customBackgroundTexture != null)
                {
                    Destroy(customBackgroundTexture);
                }

                customBackgroundTexture = loaded;
                if (customScaledLayer != null)
                {
                    customScaledLayer.style.backgroundImage = Background.FromTexture2D(customBackgroundTexture);
                }
                if (customUnscaledLayer != null)
                {
                    customUnscaledLayer.style.backgroundImage = Background.FromTexture2D(customBackgroundTexture);
                }
                if (customScaledLayer == null && customUnscaledLayer == null)
                {
                    bg.style.backgroundImage = Background.FromTexture2D(customBackgroundTexture);
                    SetBackgroundScaleMode(bg, GetCustomBackgroundScaleMode());
                }

                bg.style.backgroundColor = Color.black;
                UpdateCustomBackgroundScaleMode();
            }
            catch
            {
                ApplyBackgroundColor(Color.black);
            }
        }

        // 设置当前 Spout 纹理到缩放/原始视图层。
        void SetTexture(RenderTexture tex, string texName = "")
        {
            currentTex = tex;
            if (tex != null)
            {
                var scaledTarget = scaled ?? bg;
                if (scaledTarget == null)
                {
                    return;
                }

                scaledTarget.style.backgroundImage = Background.FromRenderTexture(tex);
                SetBackgroundScaleMode(scaledTarget, ScaleMode.ScaleToFit);
                if (unscaled != null)
                {
                    unscaled.style.backgroundImage = Background.FromRenderTexture(tex);
                    unscaled.style.width = tex.width;
                    unscaled.style.height = tex.height;
                }
                if (customUnscaledLayer != null)
                {
                    customUnscaledLayer.style.width = tex.width;
                    customUnscaledLayer.style.height = tex.height;
                }
                EnsureScaledViewportFill();
                currentTex.name = texName;
                UpdateCustomBackgroundScaleMode();
            }
        }

        // 镜像显示开关（通过 X 轴缩放翻转）。
        void SetMirror(bool isMirrored)
        {
            var x = isMirrored ? -1f : 1f;
            var scaledTarget = scaled ?? bg;
            if (scaledTarget != null)
            {
                scaledTarget.style.scale = new StyleScale(new Scale(new Vector3(x, 1f, 1f)));
            }
            if (unscaled != null)
            {
                unscaled.style.scale = new StyleScale(new Scale(new Vector3(x, 1f, 1f)));
            }
            if (customScaledLayer != null)
            {
                customScaledLayer.style.scale = new StyleScale(new Scale(new Vector3(x, 1f, 1f)));
            }
            if (customUnscaledLayer != null)
            {
                customUnscaledLayer.style.scale = new StyleScale(new Scale(new Vector3(x, 1f, 1f)));
            }
        }
        // 切换全屏与窗口模式。
        void SetFullScreen(bool isFullScreen)
        {
            var currentlyFullscreen = IsFullscreenActive();
            if (isFullScreen == currentlyFullscreen)
            {
                return;
            }

            if (isFullScreen)
            {
                windowedWidth = Screen.width;
                windowedHeight = Screen.height;

                GetTargetMonitorResolution(out var nativeWidth, out var nativeHeight);
                Screen.SetResolution(nativeWidth, nativeHeight, FullScreenMode.FullScreenWindow);
            }
            else
            {
                Screen.SetResolution(windowedWidth, windowedHeight, FullScreenMode.Windowed);
            }

            EnsureScaledViewportFill();
        }

        // 判断当前是否处于全屏态（容忍少量分辨率偏差）。
        bool IsFullscreenActive()
        {
            if (!Screen.fullScreen || Screen.fullScreenMode == FullScreenMode.Windowed)
            {
                return false;
            }

            GetTargetMonitorResolution(out var nativeWidth, out var nativeHeight);

            if (nativeWidth <= 0 || nativeHeight <= 0)
            {
                return true;
            }

            const int tolerance = 8;
            var widthMatch = Mathf.Abs(Screen.width - nativeWidth) <= tolerance;
            var heightMatch = Mathf.Abs(Screen.height - nativeHeight) <= tolerance;
            return widthMatch && heightMatch;
        }

        // 获取当前窗口所在显示器的目标分辨率。
        void GetTargetMonitorResolution(out int width, out int height)
        {
            width = Screen.currentResolution.width;
            height = Screen.currentResolution.height;

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            if (TryGetWindowMonitorResolution(out var monitorWidth, out var monitorHeight))
            {
                width = monitorWidth;
                height = monitorHeight;
                return;
            }
#endif

            if (Screen.width > 0 && Screen.height > 0)
            {
                width = Screen.width;
                height = Screen.height;
            }
        }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        // Windows 下通过窗口句柄查询显示器分辨率。
        bool TryGetWindowMonitorResolution(out int width, out int height)
        {
            width = 0;
            height = 0;

            var hwnd = GetActiveWindow();
            if (hwnd == System.IntPtr.Zero)
            {
                hwnd = GetCurrentProcessWindowHandle();
            }
            if (hwnd == System.IntPtr.Zero)
            {
                return false;
            }

            var hMonitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (hMonitor == System.IntPtr.Zero)
            {
                return false;
            }

            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(hMonitor, ref monitorInfo))
            {
                return false;
            }

            width = monitorInfo.rcMonitor.right - monitorInfo.rcMonitor.left;
            height = monitorInfo.rcMonitor.bottom - monitorInfo.rcMonitor.top;
            return width > 0 && height > 0;
        }
#endif

        // 保证缩放层始终铺满视口。
        void EnsureScaledViewportFill()
        {
            var scaledTarget = scaled ?? bg;
            if (scaledTarget == null)
            {
                return;
            }

            scaledTarget.style.left = 0f;
            scaledTarget.style.top = 0f;
            scaledTarget.style.right = 0f;
            scaledTarget.style.bottom = 0f;
            scaledTarget.style.width = StyleKeyword.Auto;
            scaledTarget.style.height = StyleKeyword.Auto;
        }

        // 根据窗口尺寸动态计算控制面板布局。
        void UpdateControlPanelLayout()
        {
            if (controlPanel == null)
            {
                return;
            }

            var rootWidth = root.resolvedStyle.width;
            var rootHeight = root.resolvedStyle.height;
            if (float.IsNaN(rootWidth) || rootWidth <= 0f)
            {
                rootWidth = Screen.width;
            }
            if (float.IsNaN(rootHeight) || rootHeight <= 0f)
            {
                rootHeight = Screen.height;
            }

            var safeWidth = Mathf.Max(240f, rootWidth - 16f);
            var safeHeight = Mathf.Max(200f, rootHeight - 16f);

            var targetWidth = Mathf.Clamp(safeWidth * 0.42f, 360f, 620f);
            targetWidth = Mathf.Min(targetWidth, safeWidth * 0.95f);

            controlPanel.style.width = targetWidth;
            controlPanel.style.maxWidth = safeWidth * 0.95f;
            controlPanel.style.maxHeight = safeHeight * 0.9f;

            var compact = safeHeight < 420f;
            controlPanel.style.paddingTop = compact ? 4f : 8f;
            controlPanel.style.paddingBottom = compact ? 2f : 4f;
            controlPanel.style.paddingLeft = compact ? 6f : 8f;
            controlPanel.style.paddingRight = compact ? 6f : 8f;
        }

        // 控制面板输入、热键与显隐逻辑更新。
        void UpdateControlPanel()
        {
            if (Application.isFocused)
            {
                if (Time.unscaledTime >= nextFullscreenProbeTime)
                {
                    cachedFullscreenState = IsFullscreenActive();
                    nextFullscreenProbeTime = Time.unscaledTime + fullscreenProbeInterval;
                }

                var fullscreenActive = cachedFullscreenState;
                if (fullScreenToggle != null && fullScreenToggle.value != fullscreenActive)
                {
                    fullScreenToggle.SetValueWithoutNotify(fullscreenActive);
                }

                var hotkeyPressed = Input.GetKeyDown(KeyCode.F11)
                    && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl))
                    && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt));
                if (hotkeyPressed)
                {
                    ToggleControlPanelVisibility();
                }

                var altEnterPressed = (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                    && (Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt));
                if (altEnterPressed)
                {
                    SetFullScreen(!fullscreenActive);
                    cachedFullscreenState = !fullscreenActive;
                    nextFullscreenProbeTime = 0f;
                    MarkControlPanelActive();
                }

                if (Input.GetMouseButtonDown(0))
                {
                    if (controlPanel != null && controlPanel.style.display == DisplayStyle.None)
                    {
                        ShowControlPanel();
                        return;
                    }
                    else if (IsPointerInsideControlPanel())
                    {
                        MarkControlPanelActive();
                    }
                }
            }

            AutoFadeControlPanel();
        }

        // 切换控制面板显示状态。
        void ToggleControlPanelVisibility()
        {
            if (controlPanel == null)
            {
                return;
            }

            if (controlPanel.style.display == DisplayStyle.None)
            {
                ShowControlPanel();
            }
            else
            {
                HideControlPanelImmediately();
            }
        }

        // 显示控制面板并恢复交互。
        void ShowControlPanel()
        {
            if (controlPanel == null)
            {
                return;
            }

            controlPanel.style.display = DisplayStyle.Flex;
            controlPanel.style.opacity = controlPanelMaxOpacity;
            SetControlPanelInteractive(true);
            lastControlPanelActivityTime = Time.unscaledTime;
        }

        // 立即隐藏控制面板并停止交互。
        void HideControlPanelImmediately()
        {
            if (controlPanel == null)
            {
                return;
            }

            isSourceDropdownInteracting = false;
            cachedDropdownPopupVisible = false;
            nextDropdownPopupScanTime = 0f;
            lastSourceDropdownInteractionTime = 0f;
            CloseSourceDropdownPopupSafely();
            SetControlPanelInteractive(false);
            controlPanel.style.opacity = 0f;
            controlPanel.style.display = DisplayStyle.None;
        }

        // 标记控制面板活跃，重置自动淡出计时。
        void MarkControlPanelActive()
        {
            if (controlPanel == null || controlPanel.style.display == DisplayStyle.None)
            {
                return;
            }

            controlPanel.style.opacity = controlPanelMaxOpacity;
            lastControlPanelActivityTime = Time.unscaledTime;
        }

        // 自动淡出控制面板，避免遮挡画面。
        void AutoFadeControlPanel()
        {
            if (controlPanel == null || controlPanel.style.display == DisplayStyle.None)
            {
                return;
            }

            var dropdownPopupVisible = IsAnyDropdownPopupVisibleThrottled();
            if (isSourceDropdownInteracting && !dropdownPopupVisible && Time.unscaledTime - lastSourceDropdownInteractionTime > 0.25f)
            {
                isSourceDropdownInteracting = false;
            }

            var isDropdownActive = isSourceDropdownInteracting || dropdownPopupVisible;
            if (isDropdownActive)
            {
                controlPanel.style.opacity = controlPanelMaxOpacity;
                SetControlPanelInteractive(true);
                lastControlPanelActivityTime = Time.unscaledTime;
                return;
            }

            var idleTime = Time.unscaledTime - lastControlPanelActivityTime;
            if (idleTime <= controlPanelIdleDelay)
            {
                controlPanel.style.opacity = controlPanelMaxOpacity;
                SetControlPanelInteractive(true);
                return;
            }

            var fadeProgress = Mathf.Clamp01((idleTime - controlPanelIdleDelay) / controlPanelFadeDuration);
            controlPanel.style.opacity = Mathf.Lerp(controlPanelMaxOpacity, 0f, fadeProgress);
            SetControlPanelInteractive(true);
            if (fadeProgress >= 1f)
            {
                SetControlPanelInteractive(false);
                controlPanel.style.display = DisplayStyle.None;
            }
        }

        // 下拉弹窗可见性检测（节流版本）。
        bool IsAnyDropdownPopupVisibleThrottled()
        {
            if (Time.unscaledTime < nextDropdownPopupScanTime)
            {
                return cachedDropdownPopupVisible;
            }

            nextDropdownPopupScanTime = Time.unscaledTime + dropdownPopupScanInterval;
            cachedDropdownPopupVisible = IsAnyDropdownPopupVisible();
            return cachedDropdownPopupVisible;
        }

        // 判断当前是否存在可见下拉弹窗。
        bool IsAnyDropdownPopupVisible()
        {
            if (root == null || root.panel == null)
            {
                return false;
            }

            var panelRoot = root.panel.visualTree;
            if (panelRoot == null)
            {
                return false;
            }

            var focusedElement = root.panel.focusController?.focusedElement as VisualElement;
            for (var current = focusedElement; current != null; current = current.parent)
            {
                if (ReferenceEquals(current, spoutDropdown))
                {
                    return true;
                }
            }

            try
            {
                foreach (var element in panelRoot.Query<VisualElement>().ToList())
                {
                    if (element.resolvedStyle.display == DisplayStyle.None || element.resolvedStyle.visibility != Visibility.Visible)
                    {
                        continue;
                    }

                    if (!IsDescendantOf(element, controlPanel) && HasPopupLikeClass(element))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        // 通过类名启发式识别弹窗元素。
        bool HasPopupLikeClass(VisualElement element)
        {
            foreach (var className in element.GetClasses())
            {
                var name = className.ToLowerInvariant();
                if (name.Contains("popup") || name.Contains("menu"))
                {
                    return true;
                }
            }

            return false;
        }

        // 判断元素是否为指定祖先的后代。
        bool IsDescendantOf(VisualElement element, VisualElement ancestor)
        {
            if (element == null || ancestor == null)
            {
                return false;
            }

            for (var current = element; current != null; current = current.parent)
            {
                if (ReferenceEquals(current, ancestor))
                {
                    return true;
                }
            }

            return false;
        }

        // 安全关闭源下拉弹窗焦点。
        void CloseSourceDropdownPopupSafely()
        {
            spoutDropdown?.Blur();
        }

        // 设置控制面板交互能力与拾取模式。
        void SetControlPanelInteractive(bool isInteractive)
        {
            if (controlPanel == null)
            {
                return;
            }

            controlPanel.SetEnabled(isInteractive);
            controlPanel.pickingMode = isInteractive ? PickingMode.Position : PickingMode.Ignore;
        }

        // 判断鼠标是否在控制面板内。
        bool IsPointerInsideControlPanel()
        {
            if (controlPanel == null || root.panel == null || controlPanel.style.display == DisplayStyle.None)
            {
                return false;
            }

            var panelPointer = RuntimePanelUtils.ScreenToPanel(root.panel, Input.mousePosition);
            return controlPanel.worldBound.Contains(panelPointer);
        }

        // 将当前纹理截图保存为 PNG。
        void SaveTexture()
        {
            if (currentTex != null)
            {
                var tmp = RenderTexture.active;
                var w = currentTex.width;
                var h = currentTex.height;
                var rt = (RenderTexture)null;
                var tex = (Texture2D)null;
                try
                {
                    rt = new RenderTexture(w, h, 0);
                    tex = new Texture2D(w, h, TextureFormat.RGBA32, false);

                    Graphics.Blit(currentTex, rt);
                    RenderTexture.active = rt;
                    tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    var data = tex.EncodeToPNG();
                    var now = System.DateTime.Now;
                    var fileName = $"{currentTex.name}_{now.Year}_{now.Month:00}{now.Day:00}_{now.Hour:00}{now.Minute:00}.png";
                    File.WriteAllBytes(fileName, data);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"Failed to save texture: {e.Message}");
                }
                finally
                {
                    RenderTexture.active = tmp;
                    if (rt != null)
                    {
                        rt.Release();
                        Destroy(rt);
                    }
                    if (tex != null)
                    {
                        Destroy(tex);
                    }
                }
            }
        }
    }

    // 应用运行时性能策略：关闭 vSync，使用用户设定帧率。
    private void ApplyRuntimePerformanceMode()
    {
        // Avoid relying on broken vSync in some multi-window / multi-monitor setups.
        QualitySettings.vSyncCount = 0;
        Application.targetFrameRate = runtimeTargetFrameRate;
    }

    // 规范化刷新率输入，确保处于合法范围。
    private static int SanitizeTargetFrameRate(int value)
    {
        if (value <= 0)
        {
            return DefaultTargetFrameRate;
        }

        return Mathf.Clamp(value, MinTargetFrameRate, MaxTargetFrameRate);
    }

    // 获取配置文件路径（优先 exe 同目录）。
    private string GetSettingsFilePath()
    {
        try
        {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                var exeDir = Path.GetDirectoryName(exePath);
                if (!string.IsNullOrWhiteSpace(exeDir))
                {
                    return Path.Combine(exeDir, SettingsFileName);
                }
            }
#endif
            var dataDir = Directory.GetParent(Application.dataPath);
            return Path.Combine(dataDir != null ? dataDir.FullName : Application.dataPath, SettingsFileName);
        }
        catch
        {
            return Path.Combine(Application.dataPath, SettingsFileName);
        }
    }

    // 读取配置文件。
    private ViewerSettings LoadSettings()
    {
        var path = GetSettingsFilePath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonUtility.FromJson<ViewerSettings>(json);
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to load settings: {e.Message}");
            return null;
        }
    }

    // 写入配置文件。
    private void SaveSettings(ViewerSettings settings)
    {
        if (settings == null)
        {
            return;
        }

        var path = GetSettingsFilePath();
        try
        {
            var json = JsonUtility.ToJson(settings, true);
            File.WriteAllText(path, json);
        }
        catch (Exception e)
        {
            Debug.LogError($"Failed to save settings: {e.Message}");
        }
    }
    // 打开图片选择对话框（按平台分发）。
    private string OpenImageFileDialog()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        return OpenWindowsImageFileDialog();
#else
        return string.Empty;
#endif
    }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    // Windows 原生文件选择框。
    private string OpenWindowsImageFileDialog()
    {
        var openFileName = new OpenFileName
        {
            dlgOwner = GetCurrentProcessWindowHandle(),
            filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.tga;*.exr;*.hdr;*.ico)\0*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.tga;*.exr;*.hdr;*.ico\0所有文件 (*.*)\0*.*\0\0",
            initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            title = "选择背景图片",
            flags = OfnExplorer | OfnPathMustExist | OfnFileMustExist | OfnHideReadOnly | OfnNoChangeDir
        };

        if (!GetOpenFileName(openFileName))
        {
            return string.Empty;
        }

        var selectedPath = openFileName.file;
        var nullIndex = selectedPath.IndexOf('\0');
        if (nullIndex >= 0)
        {
            selectedPath = selectedPath.Substring(0, nullIndex);
        }

        return selectedPath;
    }

    // 获取当前进程对应的主窗口句柄，避免多开串窗。
    private IntPtr GetCurrentProcessWindowHandle()
    {
        var activeHandle = GetActiveWindow();
        if (IsWindowFromCurrentProcess(activeHandle))
        {
            return activeHandle;
        }

        var currentProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
        IntPtr matchedHandle = IntPtr.Zero;
        EnumWindows((hWnd, lParam) =>
        {
            if (!IsWindowVisible(hWnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hWnd, out var processId);
            if (processId != currentProcessId)
            {
                return true;
            }

            matchedHandle = hWnd;
            return false;
        }, IntPtr.Zero);

        return matchedHandle;
    }

    // 判断句柄是否属于当前进程。
    private bool IsWindowFromCurrentProcess(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(hWnd, out var processId);
        return processId == (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
    }
#endif
}


































