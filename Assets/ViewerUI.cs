using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.UIElements;

using Klak.Spout;
using UniRx;
public class ViewerUI : MonoBehaviour
{
    private bool initialized;
    private VisualElement boundRoot;
    private Texture2D customBackgroundTexture;
    private readonly CompositeDisposable runtimeSubscriptions = new CompositeDisposable();
    private const string SettingsFileName = "viewer-settings.json";

    [Serializable]
    private class ViewerSettings
    {
        public string spoutSourceName = string.Empty;
        public string backgroundMode = "默认";
        public string backgroundPath = string.Empty;
        public bool scaleToScreen = true;
        public bool mirror;
        public bool fullScreen;
        public bool topMost;
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

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern System.IntPtr FindWindow(string lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern System.IntPtr MonitorFromWindow(System.IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(System.IntPtr hMonitor, ref MONITORINFO lpmi);

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
        runtimeSubscriptions.Dispose();
        if (customBackgroundTexture != null)
        {
            Destroy(customBackgroundTexture);
            customBackgroundTexture = null;
        }
    }

    private void OnEnable()
    {
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
            customScaledLayer.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
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
            customUnscaledLayer.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
            customUnscaledLayer.pickingMode = PickingMode.Ignore;
            unscaledContent.Insert(0, customUnscaledLayer);
        }
        var controlPanel = root.Q<VisualElement>("ControlPanel");
        var spoutDropdown = root.Q<DropdownField>("SpoutNames");
        var backgroundModeDropdown = root.Q<DropdownField>("BgMode");
        var backgroundPathField = root.Q<TextField>("BgPath");
        var scrollView = root.Q<ScrollView>("UnscaledScroll");
        var topMost = root.Q<Toggle>("TopMost");
        var scaleToggle = root.Q<Toggle>("TexScale");
        var mirrorToggle = root.Q<Toggle>("Mirror");
        var fullScreenToggle = root.Q<Toggle>("FullScreen");
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
        var lastBackgroundPickerOpenTime = -10f;
        var windowedWidth = Screen.width;
        var windowedHeight = Screen.height;
        var lastScreenWidth = Screen.width;
        var lastScreenHeight = Screen.height;
        var loadedSettings = LoadSettings();

        if (scaled != null)
        {
            scaled.style.backgroundColor = Color.clear;
        }
        if (scrollView != null)
        {
            scrollView.style.backgroundColor = Color.clear;
        }

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
                SetTexture(spoutReceiver.receivedTexture, spoutDropdown.value);
                spoutDropdown.choices = SpoutManager.GetSourceNames().ToList();
                MarkControlPanelActive();
            });
            spoutDropdown.RegisterCallback<PointerDownEvent>(_ =>
            {
                isSourceDropdownInteracting = true;
                lastSourceDropdownInteractionTime = Time.unscaledTime;
                MarkControlPanelActive();
            });
            spoutDropdown.RegisterCallback<FocusOutEvent>(_ => lastSourceDropdownInteractionTime = Time.unscaledTime);
            spoutDropdown.RegisterValueChangedCallback(_ =>
            {
                isSourceDropdownInteracting = false;
                lastSourceDropdownInteractionTime = Time.unscaledTime;
            });
        }

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
                alwaysOnTop.AssignTopmostWindow(Application.productName, evt.newValue);
            }
            MarkControlPanelActive();
        });

        if (alwaysOnTop != null && topMost != null)
        {
            alwaysOnTop.AssignTopmostWindow(Application.productName, topMost.value);
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
            var initialFullscreen = loadedSettings != null ? loadedSettings.fullScreen : IsFullscreenActive();
            fullScreenToggle.SetValueWithoutNotify(initialFullscreen);
            fullScreenToggle.RegisterValueChangedCallback(evt =>
            {
                SetFullScreen(evt.newValue);
                MarkControlPanelActive();
            });
            if (initialFullscreen != IsFullscreenActive())
            {
                SetFullScreen(initialFullscreen);
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
                    topMost = topMost != null && topMost.value
                });
                MarkControlPanelActive();
            };
        }

        controlPanel?.RegisterCallback<PointerDownEvent>(_ => MarkControlPanelActive());
        root.RegisterCallback<GeometryChangedEvent>(_ => { UpdateControlPanelLayout(); UpdateCustomBackgroundScaleMode(); });

        HideControlPanelImmediately();
        UpdateControlPanelLayout();

        spoutReceiver.ObserveEveryValueChanged(r => r.receivedTexture)
            .Subscribe(tex => SetTexture(tex, spoutDropdown != null ? spoutDropdown.value : string.Empty))
            .AddTo(runtimeSubscriptions);

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

                UpdateControlPanel();
            })
            .AddTo(runtimeSubscriptions);

        initialized = true;

        bool IsCustomBackgroundMode()
        {
            return backgroundModeDropdown != null && backgroundModeDropdown.value == "自定义";
        }

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

        ScaleMode GetCustomBackgroundScaleMode()
        {
            return scaleToggle != null && scaleToggle.value
                ? ScaleMode.ScaleToFit
                : ScaleMode.ScaleAndCrop;
        }

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
                    customScaledLayer.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
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
                customUnscaledLayer.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
                customUnscaledLayer.style.display = DisplayStyle.Flex;
            }
        }

        void UpdateBackgroundControlsVisibility()
        {
            var showCustom = IsCustomBackgroundMode();
            if (backgroundPathField != null)
            {
                backgroundPathField.style.display = showCustom ? DisplayStyle.Flex : DisplayStyle.None;
            }
        }

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
                    bg.style.unityBackgroundScaleMode = GetCustomBackgroundScaleMode();
                }

                bg.style.backgroundColor = Color.black;
                UpdateCustomBackgroundScaleMode();
            }
            catch
            {
                ApplyBackgroundColor(Color.black);
            }
        }

        void SetTexture(RenderTexture tex, string texName = "")
        {
            currentTex = tex;
            if (tex != null)
            {
                var scaledTarget = scaled ?? bg;
                scaledTarget.style.backgroundImage = Background.FromRenderTexture(tex);
                scaledTarget.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
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

        void SetMirror(bool isMirrored)
        {
            var x = isMirrored ? -1f : 1f;
            var scaledTarget = scaled ?? bg;
            scaledTarget.transform.scale = new Vector3(x, 1f, 1f);
            if (unscaled != null)
            {
                unscaled.transform.scale = new Vector3(x, 1f, 1f);
            }
            if (customScaledLayer != null)
            {
                customScaledLayer.transform.scale = new Vector3(x, 1f, 1f);
            }
            if (customUnscaledLayer != null)
            {
                customUnscaledLayer.transform.scale = new Vector3(x, 1f, 1f);
            }
        }
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
        bool TryGetWindowMonitorResolution(out int width, out int height)
        {
            width = 0;
            height = 0;

            var hwnd = GetActiveWindow();
            if (hwnd == System.IntPtr.Zero)
            {
                hwnd = FindWindow(null, Application.productName);
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

        void UpdateControlPanel()
        {
            if (Application.isFocused)
            {
                var fullscreenActive = IsFullscreenActive();
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

        void HideControlPanelImmediately()
        {
            if (controlPanel == null)
            {
                return;
            }

            isSourceDropdownInteracting = false;
            lastSourceDropdownInteractionTime = 0f;
            CloseSourceDropdownPopupSafely();
            SetControlPanelInteractive(false);
            controlPanel.style.opacity = 0f;
            controlPanel.style.display = DisplayStyle.None;
        }

        void MarkControlPanelActive()
        {
            if (controlPanel == null || controlPanel.style.display == DisplayStyle.None)
            {
                return;
            }

            controlPanel.style.opacity = controlPanelMaxOpacity;
            lastControlPanelActivityTime = Time.unscaledTime;
        }

        void AutoFadeControlPanel()
        {
            if (controlPanel == null || controlPanel.style.display == DisplayStyle.None)
            {
                return;
            }

            var dropdownPopupVisible = IsAnyDropdownPopupVisible();
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

            return false;
        }

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

        void CloseSourceDropdownPopupSafely()
        {
            spoutDropdown?.Blur();
        }

        void SetControlPanelInteractive(bool isInteractive)
        {
            if (controlPanel == null)
            {
                return;
            }

            controlPanel.SetEnabled(isInteractive);
            controlPanel.pickingMode = isInteractive ? PickingMode.Position : PickingMode.Ignore;
        }

        bool IsPointerInsideControlPanel()
        {
            if (controlPanel == null || root.panel == null || controlPanel.style.display == DisplayStyle.None)
            {
                return false;
            }

            var panelPointer = RuntimePanelUtils.ScreenToPanel(root.panel, Input.mousePosition);
            return controlPanel.worldBound.Contains(panelPointer);
        }

        void SaveTexture()
        {
            if (currentTex != null)
            {
                var tmp = RenderTexture.active;

                var w = currentTex.width;
                var h = currentTex.height;
                var rt = new RenderTexture(w, h, 0);
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);

                Graphics.Blit(currentTex, rt);
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                var data = tex.EncodeToPNG();
                var now = System.DateTime.Now;
                var fileNmae = $"{currentTex.name}_{now.Year}_{now.Month:00}{now.Day:00}_{now.Hour:00}{now.Minute:00}.png";
                try
                {
                    File.WriteAllBytes(fileNmae, data);
                }
                catch (System.Exception e)
                {
                    Debug.LogError($"Failed to save texture: {e.Message}");
                }

                RenderTexture.active = tmp;
                rt.Release();
                Destroy(rt);
                Destroy(tex);
            }
        }
    }

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
    private string OpenImageFileDialog()
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        return OpenWindowsImageFileDialog();
#else
        return string.Empty;
#endif
    }

#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
    private string OpenWindowsImageFileDialog()
    {
        var openFileName = new OpenFileName
        {
            dlgOwner = GetActiveWindow(),
            filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.tga;*.exr;*.hdr;*.ico)\0*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.tga;*.exr;*.hdr;*.ico\0所有文件 (*.*)\0*.*\0\0",
            initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            title = "选择背景图片",
            flags = OfnExplorer | OfnPathMustExist | OfnFileMustExist | OfnHideReadOnly | OfnNoChangeDir
        };

        if (openFileName.dlgOwner == IntPtr.Zero)
        {
            openFileName.dlgOwner = FindWindow(null, Application.productName);
        }

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
#endif
}


































