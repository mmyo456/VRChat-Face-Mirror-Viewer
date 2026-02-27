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
    private readonly CompositeDisposable runtimeSubscriptions = new CompositeDisposable();

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
#endif

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
        var controlPanel = root.Q<VisualElement>("ControlPanel");
        var spoutDropdown = root.Q<DropdownField>("SpoutNames");
        var scrollView = root.Q<ScrollView>("UnscaledScroll");
        var topMost = root.Q<Toggle>("TopMost");
        var scaleToggle = root.Q<Toggle>("TexScale");
        var mirrorToggle = root.Q<Toggle>("Mirror");
        var fullScreenToggle = root.Q<Toggle>("FullScreen");
        var saveButton = root.Q<Button>();

        var spoutReceiver = GetComponent<SpoutReceiver>();
        if (spoutReceiver == null)
        {
            return;
        }
        var currentTex = (RenderTexture)null;
        var lastControlPanelActivityTime = Time.unscaledTime;
        var isSourceDropdownInteracting = false;
        var lastSourceDropdownInteractionTime = Time.unscaledTime;
        var windowedWidth = Screen.width;
        var windowedHeight = Screen.height;
        var lastScreenWidth = Screen.width;
        var lastScreenHeight = Screen.height;

        if (spoutDropdown != null)
        {
            spoutDropdown.style.display = DisplayStyle.Flex;
            spoutDropdown.choices = SpoutManager.GetSourceNames().ToList();
            spoutDropdown.RegisterValueChangedCallback(evt => spoutReceiver.sourceName = evt.newValue);
            spoutDropdown.RegisterCallback<FocusInEvent>(evt =>
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
            spoutDropdown.RegisterCallback<FocusOutEvent>(_ =>
            {
                lastSourceDropdownInteractionTime = Time.unscaledTime;
            });
            spoutDropdown.RegisterValueChangedCallback(_ =>
            {
                isSourceDropdownInteracting = false;
                lastSourceDropdownInteractionTime = Time.unscaledTime;
            });
        }

        topMost?.RegisterValueChangedCallback(evt =>
        {
            if (alwaysOnTop != null)
            {
                alwaysOnTop.AssignTopmostWindow(Application.productName, evt.newValue);
            }
            MarkControlPanelActive();
        });
        scaleToggle?.RegisterValueChangedCallback(evt =>
        {
            if (scrollView != null)
            {
                scrollView.style.display = evt.newValue ? DisplayStyle.None : DisplayStyle.Flex;
            }
            MarkControlPanelActive();
        });
        if (scaleToggle != null && scrollView != null)
        {
            scaleToggle.SetValueWithoutNotify(true);
            scrollView.style.display = DisplayStyle.None;
        }
        if (mirrorToggle != null)
        {
            mirrorToggle.RegisterValueChangedCallback(evt =>
            {
                SetMirror(evt.newValue);
                MarkControlPanelActive();
            });
            SetMirror(mirrorToggle.value);
        }
        if (fullScreenToggle != null)
        {
            fullScreenToggle.SetValueWithoutNotify(IsFullscreenActive());
            fullScreenToggle.RegisterValueChangedCallback(evt =>
            {
                SetFullScreen(evt.newValue);
                MarkControlPanelActive();
            });
        }
        if (saveButton != null)
        {
            saveButton.clicked += () =>
            {
                SaveTexture();
                MarkControlPanelActive();
            };
        }
        controlPanel?.RegisterCallback<PointerDownEvent>(_ => MarkControlPanelActive());
        root.RegisterCallback<GeometryChangedEvent>(_ => UpdateControlPanelLayout());
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
                }

                UpdateControlPanel();
            })
            .AddTo(runtimeSubscriptions);

        initialized = true;

        void SetTexture(RenderTexture tex, string texName = "")
        {
            currentTex = tex;
            if (tex != null)
            {
                var scaledTarget = scaled ?? bg;
                scaledTarget.style.backgroundImage = Background.FromRenderTexture(tex);
                scaledTarget.style.unityBackgroundScaleMode = ScaleMode.ScaleToFit;
                unscaled.style.backgroundImage = Background.FromRenderTexture(tex);
                unscaled.style.width = tex.width;
                unscaled.style.height = tex.height;
                EnsureScaledViewportFill();
                currentTex.name = texName;
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
            if (isSourceDropdownInteracting
                && !dropdownPopupVisible
                && Time.unscaledTime - lastSourceDropdownInteractionTime > 0.25f)
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
            if (spoutDropdown != null)
            {
                spoutDropdown.Blur();
            }
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
}


