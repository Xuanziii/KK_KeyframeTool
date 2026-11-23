using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using BepInEx;
using BepInEx.Configuration;
using ExtensibleSaveFormat;
using KKAPI;
using KKAPI.Studio;
using KKAPI.Studio.SaveLoad;
using KKAPI.Utilities;
using Studio;
using UnityEngine;

namespace KeyframeToolPlugin
{
    [BepInPlugin(GUID, PluginName, Version)]
    [BepInProcess("CharaStudio")]
    [BepInDependency(KoikatuAPI.GUID, KoikatuAPI.VersionConst)]
    [BepInDependency(ExtendedSave.GUID, "16.8.1")]
    public class KeyframeTool : BaseUnityPlugin
    {
        public const string GUID = "com.custom.koikatsu.keyframetool";
        public const string PluginName = "KeyframeTool";
        public const string Version = "5.0.0";
        public const string ExtDataKey = "KeyframeToolData";

        public static KeyframeTool Instance { get; private set; }

        private ConfigEntry<KeyboardShortcut> _toggleWindowKey;
        private ConfigEntry<KeyboardShortcut> _playPauseKey;
        private ConfigEntry<bool> _darkMode;

        // 視窗與版面配置（支援右下角自由拖曳縮放）
        private bool _showWindow = true;
        private Rect _windowRect = new Rect(40, 440, 1200, 420);
        private Vector2 _trackScrollPos = Vector2.zero;
        private Vector2 _inspectorScrollPos = Vector2.zero;

        // 時間軸、縮放（Zoom）與水平捲動狀態
        private float _playbackTime = 0f;
        private float _duration = 10f;
        private float _zoomLevel = 1f;       // 1.0x ~ 10.0x
        private float _viewStartRatio = 0f;  // 0.0 ~ (1 - 1/Zoom)
        private bool _isPlaying = false;
        private bool _loop = true;
        private bool _autoKey = false;
        private bool _graphEditorMode = false;

        // 拖曳與框選互動狀態
        private bool _isScrubbingRuler = false;
        private bool _isDraggingScrollbar = false;
        private float _scrollbarDragOffset = 0f;
        private bool _isResizingWindow = false;
        private Vector2 _resizeStartMouse = Vector2.zero;
        private Vector2 _resizeStartSize = Vector2.zero;

        // 關鍵影格整批拖曳 / Time-Stretch 狀態
        private bool _isDraggingKeys = false;
        private bool _isTimeStretching = false;
        private float _dragStartTime = 0f;
        private float _stretchAnchorTime = 0f;
        private float _stretchInitialSpan = 0f;
        private readonly Dictionary<KeyframeData, float> _dragInitialTimes = new Dictionary<KeyframeData, float>();

        // 框選（Marquee Selection）狀態（以 ScrollView 內容座標記錄）
        private bool _isMarqueeSelecting = false;
        private Vector2 _marqueeStartContent = Vector2.zero;
        private Vector2 _marqueeCurrentContent = Vector2.zero;
        private bool _marqueeShiftAdditive = false;

        // Graph Editor 切線拖曳狀態
        private int _draggingHandleType = 0; // 0: 無, 1: OutHandle, 2: InHandle

        // 多重選取與跨軌道剪貼簿
        private ObjectTrack _selectedRootTrack = null;
        private SubTrack _selectedSubTrack = null;
        private KeyframeData _primarySelectedKey = null;
        private readonly HashSet<KeyframeData> _selectedKeys = new HashSet<KeyframeData>();
        private readonly List<ClipboardKeyEntry> _clipboardEntries = new List<ClipboardKeyEntry>();

        // 精準數值輸入框
        private string _timeInputStr = "0.00";
        private string _durationInputStr = "10.00";
        private string _zoomInputStr = "1.00";
        private string _posXStr = "0", _posYStr = "0", _posZStr = "0";
        private string _rotXStr = "0", _rotYStr = "0", _rotZStr = "0";
        private string _scaleXStr = "1", _scaleYStr = "1", _scaleZStr = "1";
        private string _faceEyeOpenStr = "1.00", _faceBlushStr = "0.00";
        private string _faceEyebrowStr = "0", _faceEyePtnStr = "0", _faceMouthPtnStr = "0";
        private string _outInfStr = "0.33", _outValStr = "0.00";
        private string _inInfStr = "0.33", _inValStr = "1.00";

        private readonly Dictionary<int, ObjectTrack> _tracks = new Dictionary<int, ObjectTrack>();
        private Texture2D _whiteTex;
        private bool _cameraWasLockedByUs = false;

        private const float LeftHeaderWidth = 340f;
        private const float RulerHeight = 28f;
        private const float TrackRowHeight = 28f;
        private const float BottomScrollbarHeight = 18f;
        private const float MinWindowWidth = 860f;
        private const float MinWindowHeight = 260f;

        private void Awake()
        {
            Instance = this;

            _toggleWindowKey = Config.Bind("Hotkeys", "Toggle KeyframeTool Window", new KeyboardShortcut(KeyCode.K, KeyCode.LeftControl), "開啟或關閉 KeyframeTool 時間軸視窗");
            _playPauseKey = Config.Bind("Hotkeys", "Play / Pause", new KeyboardShortcut(KeyCode.P, KeyCode.LeftControl), "播放或暫停時間軸（預設 Ctrl+P，避免與 Studio 空白鍵衝突）");
            _darkMode = Config.Bind("Appearance", "Dark Mode", true, "深色 / 淺色主題切換");

            _whiteTex = new Texture2D(1, 1);
            _whiteTex.SetPixel(0, 0, Color.white);
            _whiteTex.Apply();

            StudioSaveLoadApi.RegisterExtraBehaviour<KeyframeToolSceneController>(ExtDataKey);
        }

        private void Update()
        {
            if (!StudioAPI.InsideStudio) return;

            if (_toggleWindowKey.Value.IsDown())
                _showWindow = !_showWindow;

            if (_playPauseKey.Value.IsDown())
                _isPlaying = !_isPlaying;

            UpdateCameraLock();

            if (_autoKey && !_isPlaying && !_isScrubbingRuler && !_isDraggingKeys && _draggingHandleType == 0)
            {
                CheckAutoKeyChanges();
            }

            if (_isPlaying)
            {
                _playbackTime += Time.deltaTime;
                if (_playbackTime > _duration)
                {
                    if (_loop)
                        _playbackTime %= _duration;
                    else
                    {
                        _playbackTime = _duration;
                        _isPlaying = false;
                    }
                }
                _timeInputStr = _playbackTime.ToString("F2");
                EvaluateAllTracks(_playbackTime);
            }
        }

        private void UpdateCameraLock()
        {
            var studio = Singleton<Studio.Studio>.Instance;
            if (studio == null || studio.cameraCtrl == null) return;

            Vector2 guiMousePos = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            bool pointerOverUI = _showWindow && _windowRect.Contains(guiMousePos);
            bool isInteracting = _isScrubbingRuler || _isDraggingScrollbar || _isDraggingKeys || _isMarqueeSelecting || _isResizingWindow || _draggingHandleType != 0;

            if (pointerOverUI || isInteracting)
            {
                if (studio.cameraCtrl.enabled)
                {
                    studio.cameraCtrl.enabled = false;
                    _cameraWasLockedByUs = true;
                }
            }
            else if (_cameraWasLockedByUs)
            {
                studio.cameraCtrl.enabled = true;
                _cameraWasLockedByUs = false;
            }
        }

        private void CheckAutoKeyChanges()
        {
            foreach (var rootTrack in _tracks.Values)
            {
                foreach (var sub in rootTrack.SubTracks)
                {
                    if (!sub.StopwatchEnabled || !sub.IsValid()) continue;
                    if (HasSubTrackChangedFromEvaluated(sub, _playbackTime))
                    {
                        RecordSubTrackKeyframe(sub, _playbackTime);
                    }
                }
            }
        }

        private bool HasSubTrackChangedFromEvaluated(SubTrack sub, float time)
        {
            if (sub.Keyframes.Count == 0) return false;
            KeyframeData eval = EvaluateSubTrackValues(sub, time);
            if (eval == null) return false;

            if (sub.Type == SubTrackType.RootTransform || sub.Type == SubTrackType.IKNode)
            {
                return Vector3.Distance(sub.TargetTransform.localPosition, eval.Position) > 0.0008f ||
                       Quaternion.Angle(sub.TargetTransform.localRotation, Quaternion.Euler(eval.Rotation)) > 0.08f ||
                       (sub.Type == SubTrackType.RootTransform && Vector3.Distance(sub.TargetTransform.localScale, eval.Scale) > 0.0008f);
            }
            else if (sub.Type == SubTrackType.FKBone)
            {
                return Quaternion.Angle(sub.TargetTransform.localRotation, Quaternion.Euler(eval.Rotation)) > 0.08f;
            }
            else if (sub.Type == SubTrackType.FaceExpression && sub.CharaCtrl?.charInfo != null)
            {
                var status = sub.CharaCtrl.charInfo.fileStatus;
                return status.eyebrowPtn != eval.EyebrowPtn ||
                       status.eyesPtn != eval.EyePtn ||
                       status.mouthPtn != eval.MouthPtn ||
                       Mathf.Abs(status.eyesOpenMax - eval.EyeOpenMax) > 0.005f ||
                       Mathf.Abs(status.hohoAkaRate - eval.BlushRate) > 0.005f;
            }
            return false;
        }

        private void OnGUI()
        {
            if (!StudioAPI.InsideStudio) return;

            if (!_showWindow)
            {
                if (GUI.Button(new Rect(10, 10, 130, 24), "KeyframeTool (AE)"))
                    _showWindow = true;
                return;
            }

            // 處理全視窗級拖曳縮放（Window Resizing）
            HandleGlobalWindowResize();

            // 處理 AE 快捷鍵（Delete 刪除、Ctrl+C/V 複製貼上、F9 Easy Ease）
            HandleTimelineKeyboardEvents();

            bool dark = _darkMode.Value;
            Color prevBg = GUI.backgroundColor;
            Color prevContent = GUI.contentColor;

            GUI.backgroundColor = dark ? new Color(0.14f, 0.14f, 0.16f, 0.98f) : new Color(0.92f, 0.92f, 0.94f, 0.98f);
            GUI.contentColor = dark ? new Color(0.90f, 0.91f, 0.93f, 1f) : new Color(0.12f, 0.12f, 0.14f, 1f);

            _windowRect.width = Mathf.Clamp(_windowRect.width, MinWindowWidth, Screen.width);
            _windowRect.height = Mathf.Clamp(_windowRect.height, MinWindowHeight, Screen.height);
            _windowRect.x = Mathf.Clamp(_windowRect.x, 0, Mathf.Max(0, Screen.width - _windowRect.width));
            _windowRect.y = Mathf.Clamp(_windowRect.y, 0, Mathf.Max(0, Screen.height - _windowRect.height));

            _windowRect = GUI.Window(948725, _windowRect, DrawAEWorkspaceWindow, "KeyframeTool — AE Multi-Track & Graph Editor");

            if (Event.current.rawType == EventType.MouseUp)
            {
                _isScrubbingRuler = false;
                _isDraggingScrollbar = false;
                _isDraggingKeys = false;
                _isTimeStretching = false;
                _isMarqueeSelecting = false;
                _isResizingWindow = false;
                _draggingHandleType = 0;
                _dragInitialTimes.Clear();
            }

            GUI.backgroundColor = prevBg;
            GUI.contentColor = prevContent;
        }

        private void HandleGlobalWindowResize()
        {
            Event e = Event.current;
            if (_isResizingWindow)
            {
                if (e.type == EventType.MouseDrag)
                {
                    Vector2 delta = e.mousePosition - _resizeStartMouse;
                    _windowRect.width = Mathf.Clamp(_resizeStartSize.x + delta.x, MinWindowWidth, Screen.width - _windowRect.x);
                    _windowRect.height = Mathf.Clamp(_resizeStartSize.y + delta.y, MinWindowHeight, Screen.height - _windowRect.y);
                    e.Use();
                }
                else if (e.rawType == EventType.MouseUp)
                {
                    _isResizingWindow = false;
                    e.Use();
                }
            }
        }

        private void ClearKeySelection()
        {
            _selectedKeys.Clear();
            _primarySelectedKey = null;
        }

        private void SelectSingleKey(SubTrack sub, KeyframeData kf)
        {
            _selectedKeys.Clear();
            _selectedKeys.Add(kf);
            _selectedSubTrack = sub;
            _primarySelectedKey = kf;
            LoadKeyframeToInputs(kf);
        }

        private void ToggleKeyInSelection(SubTrack sub, KeyframeData kf)
        {
            if (_selectedKeys.Contains(kf))
            {
                _selectedKeys.Remove(kf);
                if (_primarySelectedKey == kf)
                    _primarySelectedKey = _selectedKeys.FirstOrDefault();
            }
            else
            {
                _selectedKeys.Add(kf);
                _selectedSubTrack = sub;
                _primarySelectedKey = kf;
                LoadKeyframeToInputs(kf);
            }
        }

        private void HandleTimelineKeyboardEvents()
        {
            Event e = Event.current;
            if (e.type != EventType.KeyDown) return;

            // 若正在文字輸入框打字則不攔截
            if (GUI.GetNameOfFocusedControl().StartsWith("NumInput")) return;

            // 1. Delete / Backspace：刪除所有已選取關鍵影格
            if ((e.keyCode == KeyCode.Delete || e.keyCode == KeyCode.Backspace) && _selectedKeys.Count > 0)
            {
                foreach (var root in _tracks.Values)
                {
                    foreach (var sub in root.SubTracks)
                    {
                        sub.Keyframes.RemoveAll(k => _selectedKeys.Contains(k));
                    }
                }
                ClearKeySelection();
                e.Use();
            }
            // 2. Ctrl + C：複製所有已選取關鍵影格（支援跨軌道與相對時間保留）
            else if (e.control && e.keyCode == KeyCode.C && _selectedKeys.Count > 0)
            {
                _clipboardEntries.Clear();
                float minTime = _selectedKeys.Min(k => k.Time);

                foreach (var root in _tracks.Values)
                {
                    foreach (var sub in root.SubTracks)
                    {
                        foreach (var kf in sub.Keyframes)
                        {
                            if (_selectedKeys.Contains(kf))
                            {
                                _clipboardEntries.Add(new ClipboardKeyEntry
                                {
                                    RootObjectId = root.ObjectId,
                                    SubTrackKey = sub.TrackKey,
                                    RelativeTimeOffset = kf.Time - minTime,
                                    Data = kf.Clone()
                                });
                            }
                        }
                    }
                }
                e.Use();
            }
            // 3. Ctrl + V：在當前播放頭位置貼上整組已複製關鍵影格
            else if (e.control && e.keyCode == KeyCode.V && _clipboardEntries.Count > 0)
            {
                ClearKeySelection();
                foreach (var entry in _clipboardEntries)
                {
                    if (!_tracks.TryGetValue(entry.RootObjectId, out ObjectTrack root)) continue;
                    SubTrack targetSub = root.SubTracks.FirstOrDefault(s => s.TrackKey == entry.SubTrackKey);
                    if (targetSub == null) continue;

                    float pasteTime = Mathf.Clamp(_playbackTime + entry.RelativeTimeOffset, 0f, _duration);
                    KeyframeData pasted = entry.Data.Clone();
                    pasted.Time = pasteTime;

                    targetSub.Keyframes.RemoveAll(k => Mathf.Abs(k.Time - pasteTime) < 0.005f);
                    targetSub.Keyframes.Add(pasted);
                    targetSub.Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));

                    _selectedKeys.Add(pasted);
                    _selectedSubTrack = targetSub;
                    _primarySelectedKey = pasted;
                }
                if (_primarySelectedKey != null)
                    LoadKeyframeToInputs(_primarySelectedKey);

                EvaluateAllTracks(_playbackTime);
                e.Use();
            }
            // 4. F9：AE Easy Ease（批次柔和漸變曲線）
            else if (e.keyCode == KeyCode.F9 && _selectedKeys.Count > 0)
            {
                foreach (var kf in _selectedKeys)
                {
                    kf.Easing = EasingMode.EaseInOut;
                    kf.OutInfluence = 0.33f;
                    kf.OutTangentValue = 0.0f;
                    kf.InInfluence = 0.33f;
                    kf.InTangentValue = 1.0f;
                }
                if (_primarySelectedKey != null)
                    LoadKeyframeToInputs(_primarySelectedKey);
                EvaluateAllTracks(_playbackTime);
                e.Use();
            }
        }

        // === 時間與畫面 X 座標雙向換算 ===
        private float VisibleWindowRatio => 1f / Mathf.Max(1f, _zoomLevel);
        private float MaxViewStartRatio => Mathf.Max(0f, 1f - VisibleWindowRatio);

        private float TimeToLaneX(float time, float laneStartX, float laneWidth)
        {
            float normalizedTime = Mathf.Clamp01(time / Mathf.Max(0.001f, _duration));
            float relative = (normalizedTime - _viewStartRatio) / VisibleWindowRatio;
            return laneStartX + relative * laneWidth;
        }

        private float LaneXToTime(float mouseX, float laneStartX, float laneWidth)
        {
            float relative = (mouseX - laneStartX) / Mathf.Max(1f, laneWidth);
            float normalizedTime = Mathf.Clamp01(_viewStartRatio + relative * VisibleWindowRatio);
            return normalizedTime * _duration;
        }

        private void ApplyZoom(float newZoom, float anchorNormalizedTime)
        {
            float clampedZoom = Mathf.Clamp(newZoom, 1f, 10f);
            float oldVisible = VisibleWindowRatio;
            float newVisible = 1f / clampedZoom;

            float anchorScreenRatio = Mathf.Clamp01((anchorNormalizedTime - _viewStartRatio) / oldVisible);
            _zoomLevel = clampedZoom;
            _viewStartRatio = Mathf.Clamp(anchorNormalizedTime - anchorScreenRatio * newVisible, 0f, MaxViewStartRatio);
            _zoomInputStr = _zoomLevel.ToString("F2");
        }

        private void DrawAEWorkspaceWindow(int id)
        {
            bool dark = _darkMode.Value;

            // === 1. 頂部控制列 ===
            GUILayout.BeginHorizontal(GUI.skin.box, GUILayout.Height(30));

            if (GUILayout.Button(_isPlaying ? "❚❚" : "▶", GUILayout.Width(32)))
                _isPlaying = !_isPlaying;

            if (GUILayout.Button("■", GUILayout.Width(28)))
            {
                _isPlaying = false;
                _playbackTime = 0f;
                _timeInputStr = "0.00";
                EvaluateAllTracks(0f);
            }

            _loop = GUILayout.Toggle(_loop, "Loop", GUILayout.Width(48));

            Color oldBg = GUI.backgroundColor;
            if (_autoKey) GUI.backgroundColor = new Color(0.95f, 0.30f, 0.30f, 1f);
            _autoKey = GUILayout.Toggle(_autoKey, "● Auto-Key", GUI.skin.button, GUILayout.Width(82));
            GUI.backgroundColor = oldBg;

            if (_graphEditorMode) GUI.backgroundColor = new Color(0.22f, 0.65f, 1f, 1f);
            _graphEditorMode = GUILayout.Toggle(_graphEditorMode, "📈 Graph", GUI.skin.button, GUILayout.Width(72));
            GUI.backgroundColor = oldBg;

            GUILayout.Space(6);
            GUI.contentColor = dark ? new Color(0.35f, 0.72f, 1f, 1f) : new Color(0.05f, 0.42f, 0.85f, 1f);
            GUILayout.Label("CTI:", GUILayout.Width(28));
            GUI.SetNextControlName("NumInput_Time");
            _timeInputStr = GUILayout.TextField(_timeInputStr, GUILayout.Width(52));
            if (GUILayout.Button("Go", GUILayout.Width(28)) && float.TryParse(_timeInputStr, out float tVal))
                SetPlaybackTime(tVal);
            GUI.contentColor = dark ? new Color(0.90f, 0.91f, 0.93f, 1f) : new Color(0.12f, 0.12f, 0.14f, 1f);

            GUILayout.Label("Dur:", GUILayout.Width(28));
            GUI.SetNextControlName("NumInput_Dur");
            _durationInputStr = GUILayout.TextField(_durationInputStr, GUILayout.Width(48));
            if (GUILayout.Button("Set", GUILayout.Width(30)) && float.TryParse(_durationInputStr, out float dVal) && dVal > 0.1f)
            {
                _duration = dVal;
                _durationInputStr = _duration.ToString("F2");
            }

            GUILayout.Label("Zoom:", GUILayout.Width(38));
            GUI.SetNextControlName("NumInput_Zoom");
            _zoomInputStr = GUILayout.TextField(_zoomInputStr, GUILayout.Width(42));
            if (GUILayout.Button("x", GUILayout.Width(22)) && float.TryParse(_zoomInputStr, out float zVal))
                ApplyZoom(zVal, _playbackTime / Mathf.Max(0.001f, _duration));

            GUILayout.Space(6);
            if (GUILayout.Button("+ 加入選取物件/角色", GUILayout.Width(140)))
                AddSelectedStudioObjects();

            GUILayout.FlexibleSpace();
            _darkMode.Value = GUILayout.Toggle(_darkMode.Value, _darkMode.Value ? "Dark" : "Light", GUILayout.Width(50));
            if (GUILayout.Button("✕", GUILayout.Width(26)))
                _showWindow = false;

            GUILayout.EndHorizontal();

            // === 2. 主工作區 ===
            Rect bodyRect = new Rect(8, 56, _windowRect.width - 16, _windowRect.height - 64);
            float inspectorWidth = 275f;
            float timelineAreaWidth = bodyRect.width - LeftHeaderWidth - inspectorWidth - 8f;
            float laneWidth = timelineAreaWidth - 16f;
            float trackAreaHeight = bodyRect.height - RulerHeight - BottomScrollbarHeight - 4f;

            // Alt + 滑鼠滾輪縮放時間軸
            Rect timelineInteractZone = new Rect(bodyRect.x + LeftHeaderWidth, bodyRect.y, laneWidth, RulerHeight + trackAreaHeight);
            Event e = Event.current;
            if (e.type == EventType.ScrollWheel && e.alt && timelineInteractZone.Contains(e.mousePosition))
            {
                float hoverTime = LaneXToTime(e.mousePosition.x, timelineInteractZone.x, laneWidth);
                float zoomFactor = e.delta.y < 0 ? 1.25f : 0.8f;
                ApplyZoom(_zoomLevel * zoomFactor, hoverTime / Mathf.Max(0.001f, _duration));
                e.Use();
            }

            // --- 2A. 時間刻度尺 ---
            Rect rulerLabelRect = new Rect(bodyRect.x, bodyRect.y, LeftHeaderWidth, RulerHeight);
            DrawRect(rulerLabelRect, dark ? new Color(0.18f, 0.18f, 0.21f) : new Color(0.84f, 0.84f, 0.87f));
            GUI.Label(new Rect(rulerLabelRect.x + 8, rulerLabelRect.y + 4, LeftHeaderWidth - 16, 20), "Hierarchy / Stopwatch / Key");

            Rect rulerRect = new Rect(bodyRect.x + LeftHeaderWidth, bodyRect.y, timelineAreaWidth, RulerHeight);
            DrawTimelineRuler(rulerRect, laneWidth, dark);

            // --- 2B. 中央主視圖：Dope Sheet 多軌道 或 Graph Editor ---
            Rect centerOuterRect = new Rect(bodyRect.x, bodyRect.y + RulerHeight, LeftHeaderWidth + timelineAreaWidth, trackAreaHeight);

            if (_graphEditorMode)
                DrawGraphEditorView(centerOuterRect, laneWidth, dark);
            else
                DrawDopeSheetView(centerOuterRect, laneWidth, dark);

            // 時間尺頂端藍色倒三角指標
            float headerPlayheadX = TimeToLaneX(_playbackTime, rulerRect.x, laneWidth);
            if (headerPlayheadX >= rulerRect.x && headerPlayheadX <= rulerRect.x + laneWidth)
            {
                DrawRect(new Rect(headerPlayheadX - 5f, rulerRect.y + 2, 10f, 12f), new Color(0.22f, 0.65f, 1f, 1f));
                DrawRect(new Rect(headerPlayheadX - 1f, rulerRect.y + 14, 2f, RulerHeight - 14), new Color(0.22f, 0.65f, 1f, 1f));
            }

            // --- 2C. 底部時間軸水平縮放捲動條 ---
            Rect scrollbarTrackRect = new Rect(bodyRect.x + LeftHeaderWidth, bodyRect.y + RulerHeight + trackAreaHeight + 2f, laneWidth, BottomScrollbarHeight);
            DrawTimelineZoomScrollbar(scrollbarTrackRect, dark);

            // --- 2D. 右側屬性檢視器 ---
            Rect inspectorRect = new Rect(bodyRect.x + LeftHeaderWidth + timelineAreaWidth + 6f, bodyRect.y, inspectorWidth, bodyRect.height);
            DrawNumericInspector(inspectorRect, dark);

            // --- 2E. 右下角視窗自由縮放控制點（Resize Grip） ---
            Rect resizeGripRect = new Rect(_windowRect.width - 18f, _windowRect.height - 18f, 18f, 18f);
            DrawWindowResizeGrip(resizeGripRect, dark);

            // 僅允許拖曳視窗最頂端標題列（高度 22px）移動視窗
            GUI.DragWindow(new Rect(0, 0, _windowRect.width - 24f, 22));
        }

        private void DrawWindowResizeGrip(Rect gripRect, bool dark)
        {
            Color gripCol = dark ? new Color(0.55f, 0.58f, 0.65f, 0.9f) : new Color(0.35f, 0.38f, 0.45f, 0.9f);
            // 繪製三條斜向縮放識別線
            DrawLine(new Vector2(gripRect.x + 12, gripRect.y + 16), new Vector2(gripRect.x + 16, gripRect.y + 12), 1.5f, gripCol);
            DrawLine(new Vector2(gripRect.x + 8, gripRect.y + 16), new Vector2(gripRect.x + 16, gripRect.y + 8), 1.5f, gripCol);
            DrawLine(new Vector2(gripRect.x + 4, gripRect.y + 16), new Vector2(gripRect.x + 16, gripRect.y + 4), 1.5f, gripCol);

            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && gripRect.Contains(e.mousePosition))
            {
                _isResizingWindow = true;
                // 轉換為螢幕全域滑鼠座標以確保連續拖曳平滑不抖動
                _resizeStartMouse = GUIUtility.GUIToScreenPoint(e.mousePosition);
                _resizeStartSize = new Vector2(_windowRect.width, _windowRect.height);
                e.Use();
            }
        }

        private void DrawTimelineZoomScrollbar(Rect trackRect, bool dark)
        {
            DrawRect(trackRect, dark ? new Color(0.09f, 0.09f, 0.11f) : new Color(0.78f, 0.78f, 0.80f));

            float thumbWidth = Mathf.Max(24f, trackRect.width * VisibleWindowRatio);
            float scrollableTrackSpan = Mathf.Max(1f, trackRect.width - thumbWidth);
            float thumbX = trackRect.x + (MaxViewStartRatio > 0f ? (_viewStartRatio / MaxViewStartRatio) * scrollableTrackSpan : 0f);
            Rect thumbRect = new Rect(thumbX, trackRect.y + 2f, thumbWidth, trackRect.height - 4f);

            Color thumbColor = _isDraggingScrollbar
                ? new Color(0.30f, 0.72f, 1f, 0.95f)
                : (dark ? new Color(0.32f, 0.34f, 0.38f, 0.95f) : new Color(0.55f, 0.56f, 0.60f, 0.95f));
            DrawRect(thumbRect, thumbColor);

            Event e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && trackRect.Contains(e.mousePosition))
            {
                _isDraggingScrollbar = true;
                if (thumbRect.Contains(e.mousePosition))
                {
                    _scrollbarDragOffset = e.mousePosition.x - thumbRect.x;
                }
                else
                {
                    _scrollbarDragOffset = thumbWidth * 0.5f;
                    float targetThumbX = Mathf.Clamp(e.mousePosition.x - _scrollbarDragOffset, trackRect.x, trackRect.x + scrollableTrackSpan);
                    _viewStartRatio = MaxViewStartRatio > 0f ? ((targetThumbX - trackRect.x) / scrollableTrackSpan) * MaxViewStartRatio : 0f;
                }
                e.Use();
            }
            else if (_isDraggingScrollbar && e.type == EventType.MouseDrag)
            {
                float targetThumbX = Mathf.Clamp(e.mousePosition.x - _scrollbarDragOffset, trackRect.x, trackRect.x + scrollableTrackSpan);
                _viewStartRatio = MaxViewStartRatio > 0f ? ((targetThumbX - trackRect.x) / scrollableTrackSpan) * MaxViewStartRatio : 0f;
                e.Use();
            }
        }

        private void DrawDopeSheetView(Rect outerRect, float laneWidth, bool dark)
        {
            int totalRows = 0;
            foreach (var tr in _tracks.Values)
            {
                totalRows++;
                if (tr.IsExpanded) totalRows += tr.SubTracks.Count;
            }

            float contentHeight = Mathf.Max(outerRect.height, totalRows * TrackRowHeight + 12f);
            Rect viewRect = new Rect(0, 0, outerRect.width - 16f, contentHeight);

            _trackScrollPos = GUI.BeginScrollView(outerRect, _trackScrollPos, viewRect);

            // 記錄每個可繪製菱形的命中框，用於框選判定
            var allDiamondEntries = new List<RenderedDiamondInfo>();

            int currentRow = 0;
            foreach (var kvp in _tracks.ToList())
            {
                ObjectTrack rootTrack = kvp.Value;
                if (rootTrack.RootTransform == null) continue;

                float parentY = currentRow * TrackRowHeight;
                Rect parentLeft = new Rect(0, parentY, LeftHeaderWidth, TrackRowHeight - 1);
                Rect parentLane = new Rect(LeftHeaderWidth, parentY, laneWidth, TrackRowHeight - 1);
                DrawRootTrackRow(rootTrack, parentLeft, parentLane, dark);
                currentRow++;

                if (rootTrack.IsExpanded)
                {
                    for (int s = 0; s < rootTrack.SubTracks.Count; s++)
                    {
                        SubTrack sub = rootTrack.SubTracks[s];
                        if (!sub.IsValid()) continue;

                        float subY = currentRow * TrackRowHeight;
                        Rect subLeft = new Rect(0, subY, LeftHeaderWidth, TrackRowHeight - 1);
                        Rect subLane = new Rect(LeftHeaderWidth, subY, laneWidth, TrackRowHeight - 1);

                        DrawSubTrackRow(rootTrack, sub, subLeft, subLane, laneWidth, dark, currentRow % 2 == 0, allDiamondEntries);
                        currentRow++;
                    }
                }
            }

            // 處理整批關鍵影格拖曳（Group Drag）與 Alt + 拖曳最外側時間伸縮（Time-Stretch）
            HandleMultiKeyDragging(laneWidth);

            // 處理關鍵影格製作區域（右側軌道背景）的點擊取消選取與滑鼠框選（Marquee Selection）
            Rect keyframeProductionZone = new Rect(LeftHeaderWidth, 0, laneWidth, contentHeight);
            HandleMarqueeAndBackgroundClick(keyframeProductionZone, allDiamondEntries);

            // 繪製藍色播放頭垂直指示線（CTI）
            float playheadX = TimeToLaneX(_playbackTime, LeftHeaderWidth, laneWidth);
            if (playheadX >= LeftHeaderWidth && playheadX <= LeftHeaderWidth + laneWidth)
            {
                DrawRect(new Rect(playheadX - 1f, 0, 2f, contentHeight), new Color(0.22f, 0.65f, 1f, 0.95f));
            }

            GUI.EndScrollView();
        }

        private void HandleMultiKeyDragging(float laneWidth)
        {
            Event e = Event.current;
            if (!_isDraggingKeys || _selectedKeys.Count == 0) return;

            if (e.type == EventType.MouseDrag)
            {
                float currentMouseTime = LaneXToTime(e.mousePosition.x, LeftHeaderWidth, laneWidth);

                if (_isTimeStretching && _stretchInitialSpan > 0.001f)
                {
                    // Alt + 拖曳最外側關鍵影格：以另一端為錨點進行等比例時間伸縮
                    float newOuterTime = Mathf.Clamp(currentMouseTime, 0f, _duration);
                    float scaleFactor = Mathf.Abs(newOuterTime - _stretchAnchorTime) / _stretchInitialSpan;
                    scaleFactor = Mathf.Max(0.05f, scaleFactor);

                    foreach (var kvp in _dragInitialTimes)
                    {
                        KeyframeData kf = kvp.Key;
                        float initT = kvp.Value;
                        float offset = initT - _stretchAnchorTime;
                        kf.Time = Mathf.Clamp(_stretchAnchorTime + offset * scaleFactor, 0f, _duration);
                    }
                }
                else
                {
                    // 一般整批平移拖曳：限制整組不超出 [0, _duration]
                    float rawDelta = currentMouseTime - _dragStartTime;
                    float minInit = _dragInitialTimes.Values.Min();
                    float maxInit = _dragInitialTimes.Values.Max();
                    float clampedDelta = Mathf.Clamp(rawDelta, -minInit, _duration - maxInit);

                    foreach (var kvp in _dragInitialTimes)
                    {
                        kvp.Key.Time = kvp.Value + clampedDelta;
                    }
                }

                // 重新排序受影響軌道並即時刷新預覽
                foreach (var root in _tracks.Values)
                {
                    foreach (var sub in root.SubTracks)
                    {
                        if (sub.Keyframes.Any(k => _selectedKeys.Contains(k)))
                            sub.Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
                    }
                }

                if (_primarySelectedKey != null)
                {
                    LoadKeyframeToInputs(_primarySelectedKey);
                    SetPlaybackTime(_primarySelectedKey.Time);
                }
                e.Use();
            }
        }

        private void HandleMarqueeAndBackgroundClick(Rect zoneRect, List<RenderedDiamondInfo> renderedDiamonds)
        {
            Event e = Event.current;

            // 在關鍵幀製作區域空白處按下左鍵（前面各菱形 hitRect 若命中已呼叫 e.Use()，所以走到這裡代表點在空白處）
            if (e.type == EventType.MouseDown && e.button == 0 && zoneRect.Contains(e.mousePosition))
            {
                _isMarqueeSelecting = true;
                _marqueeStartContent = e.mousePosition;
                _marqueeCurrentContent = e.mousePosition;
                _marqueeShiftAdditive = e.shift;

                // 若未按 Shift，點擊空白處立即取消目前選取的關鍵幀
                if (!e.shift)
                {
                    ClearKeySelection();
                }
                e.Use();
            }
            else if (_isMarqueeSelecting && e.type == EventType.MouseDrag)
            {
                _marqueeCurrentContent = new Vector2(
                    Mathf.Clamp(e.mousePosition.x, zoneRect.x, zoneRect.xMax),
                    Mathf.Clamp(e.mousePosition.y, zoneRect.y, zoneRect.yMax)
                );

                Rect selectionBox = GetNormalizedRect(_marqueeStartContent, _marqueeCurrentContent);
                if (!_marqueeShiftAdditive)
                    _selectedKeys.Clear();

                foreach (var item in renderedDiamonds)
                {
                    if (selectionBox.Contains(item.Center))
                    {
                        _selectedKeys.Add(item.Key);
                        _selectedSubTrack = item.SubTrack;
                        _primarySelectedKey = item.Key;
                    }
                }

                if (_primarySelectedKey != null)
                    LoadKeyframeToInputs(_primarySelectedKey);

                e.Use();
            }

            // 繪製藍色半透明框選矩形
            if (_isMarqueeSelecting)
            {
                Rect box = GetNormalizedRect(_marqueeStartContent, _marqueeCurrentContent);
                if (box.width > 2f || box.height > 2f)
                {
                    DrawRect(box, new Color(0.22f, 0.65f, 1f, 0.22f));
                    DrawRect(new Rect(box.x, box.y, box.width, 1f), new Color(0.30f, 0.75f, 1f, 0.9f));
                    DrawRect(new Rect(box.x, box.yMax - 1f, box.width, 1f), new Color(0.30f, 0.75f, 1f, 0.9f));
                    DrawRect(new Rect(box.x, box.y, 1f, box.height), new Color(0.30f, 0.75f, 1f, 0.9f));
                    DrawRect(new Rect(box.xMax - 1f, box.y, 1f, box.height), new Color(0.30f, 0.75f, 1f, 0.9f));
                }
            }
        }

        private Rect GetNormalizedRect(Vector2 p1, Vector2 p2)
        {
            float x = Mathf.Min(p1.x, p2.x);
            float y = Mathf.Min(p1.y, p2.y);
            float w = Mathf.Abs(p1.x - p2.x);
            float h = Mathf.Abs(p1.y - p2.y);
            return new Rect(x, y, w, h);
        }

        // === After Effects 風格貝茲速度曲線編輯器（Graph Editor） ===
        private void DrawGraphEditorView(Rect outerRect, float laneWidth, bool dark)
        {
            Rect leftPanel = new Rect(outerRect.x, outerRect.y, LeftHeaderWidth, outerRect.height);
            DrawRect(leftPanel, dark ? new Color(0.16f, 0.17f, 0.19f) : new Color(0.88f, 0.88f, 0.90f));

            GUILayout.BeginArea(new Rect(leftPanel.x + 8, leftPanel.y + 8, leftPanel.width - 16, leftPanel.height - 16));
            GUILayout.Label("📈 Graph Editor (速度/插值曲線)", GUI.skin.box);
            if (_selectedSubTrack != null)
            {
                GUILayout.Label($"當前軌道: {_selectedSubTrack.DisplayName}");
                GUILayout.Label($"關鍵影格數: {_selectedSubTrack.Keyframes.Count}");
                GUILayout.Space(6);
                GUILayout.Label("操作方式：\n• 點擊關鍵影格節點（◆）或按 F9 套用柔和漸變\n• 拖曳橘色／藍色控制柄調整出入加速度\n• 點擊空白處可取消選取");
            }
            else
            {
                GUILayout.Label("請先在時間軸中點選任一軌道或關鍵影格，再開啟 Graph Editor 編輯速度曲線。");
            }
            GUILayout.EndArea();

            Rect graphCanvas = new Rect(outerRect.x + LeftHeaderWidth, outerRect.y, laneWidth, outerRect.height);
            DrawRect(graphCanvas, dark ? new Color(0.10f, 0.10f, 0.12f) : new Color(0.82f, 0.82f, 0.85f));

            float padY = 24f;
            float plotHeight = graphCanvas.height - padY * 2f;
            for (int g = 0; g <= 4; g++)
            {
                float ratio = g / 4f;
                float gy = graphCanvas.y + padY + (1f - ratio) * plotHeight;
                DrawRect(new Rect(graphCanvas.x, gy, graphCanvas.width, 1f), dark ? new Color(0.20f, 0.21f, 0.24f) : new Color(0.72f, 0.72f, 0.75f));
            }

            float playheadX = TimeToLaneX(_playbackTime, graphCanvas.x, graphCanvas.width);
            if (playheadX >= graphCanvas.x && playheadX <= graphCanvas.xMax)
            {
                DrawRect(new Rect(playheadX - 1f, graphCanvas.y, 2f, graphCanvas.height), new Color(0.22f, 0.65f, 1f, 0.9f));
            }

            if (_selectedSubTrack == null || _selectedSubTrack.Keyframes.Count == 0) return;

            var keys = _selectedSubTrack.Keyframes;
            Event e = Event.current;

            for (int i = 0; i < keys.Count - 1; i++)
            {
                KeyframeData k1 = keys[i];
                KeyframeData k2 = keys[i + 1];

                float x1 = TimeToLaneX(k1.Time, graphCanvas.x, graphCanvas.width);
                float x2 = TimeToLaneX(k2.Time, graphCanvas.x, graphCanvas.width);
                float yStart = graphCanvas.y + padY + plotHeight;
                float yEnd = graphCanvas.y + padY;

                int segments = 32;
                Vector2 prevPt = new Vector2(x1, yStart);
                for (int s = 1; s <= segments; s++)
                {
                    float u = (float)s / segments;
                    float easedU = EvaluateEasingFactor(k1, k2, u);
                    float px = Mathf.Lerp(x1, x2, u);
                    float py = Mathf.Lerp(yStart, yEnd, easedU);
                    Vector2 curPt = new Vector2(px, py);

                    if ((px >= graphCanvas.x && px <= graphCanvas.xMax) || (prevPt.x >= graphCanvas.x && prevPt.x <= graphCanvas.xMax))
                    {
                        DrawLine(prevPt, curPt, 2f, new Color(0.30f, 0.82f, 0.55f, 1f));
                    }
                    prevPt = curPt;
                }

                if (_primarySelectedKey == k1 && k1.Easing != EasingMode.Hold)
                {
                    float handleX = Mathf.Lerp(x1, x2, Mathf.Clamp(k1.OutInfluence, 0.05f, 0.95f));
                    float handleY = Mathf.Lerp(yStart, yEnd, Mathf.Clamp(k1.OutTangentValue, -0.2f, 1.2f));
                    Vector2 handlePos = new Vector2(handleX, handleY);

                    DrawLine(new Vector2(x1, yStart), handlePos, 1.5f, new Color(1f, 0.72f, 0.22f));
                    DrawRect(new Rect(handlePos.x - 5f, handlePos.y - 5f, 10f, 10f), new Color(1f, 0.72f, 0.22f));

                    Rect hit = new Rect(handlePos.x - 8f, handlePos.y - 8f, 16f, 16f);
                    if (e.type == EventType.MouseDown && e.button == 0 && hit.Contains(e.mousePosition))
                    {
                        k1.Easing = EasingMode.CustomBezier;
                        _draggingHandleType = 1;
                        e.Use();
                    }
                    else if (_draggingHandleType == 1 && e.type == EventType.MouseDrag)
                    {
                        k1.OutInfluence = Mathf.Clamp((e.mousePosition.x - x1) / Mathf.Max(1f, x2 - x1), 0.05f, 0.95f);
                        k1.OutTangentValue = Mathf.Clamp((yStart - e.mousePosition.y) / Mathf.Max(1f, yStart - yEnd), 0f, 1f);
                        LoadKeyframeToInputs(k1);
                        EvaluateAllTracks(_playbackTime);
                        e.Use();
                    }
                }

                if (_primarySelectedKey == k2 && k1.Easing != EasingMode.Hold)
                {
                    float handleX = Mathf.Lerp(x2, x1, Mathf.Clamp(k2.InInfluence, 0.05f, 0.95f));
                    float handleY = Mathf.Lerp(yEnd, yStart, 1f - Mathf.Clamp(k2.InTangentValue, -0.2f, 1.2f));
                    Vector2 handlePos = new Vector2(handleX, handleY);

                    DrawLine(new Vector2(x2, yEnd), handlePos, 1.5f, new Color(0.35f, 0.75f, 1f));
                    DrawRect(new Rect(handlePos.x - 5f, handlePos.y - 5f, 10f, 10f), new Color(0.35f, 0.75f, 1f));

                    Rect hit = new Rect(handlePos.x - 8f, handlePos.y - 8f, 16f, 16f);
                    if (e.type == EventType.MouseDown && e.button == 0 && hit.Contains(e.mousePosition))
                    {
                        k1.Easing = EasingMode.CustomBezier;
                        _draggingHandleType = 2;
                        e.Use();
                    }
                    else if (_draggingHandleType == 2 && e.type == EventType.MouseDrag)
                    {
                        k2.InInfluence = Mathf.Clamp((x2 - e.mousePosition.x) / Mathf.Max(1f, x2 - x1), 0.05f, 0.95f);
                        k2.InTangentValue = Mathf.Clamp((yStart - e.mousePosition.y) / Mathf.Max(1f, yStart - yEnd), 0f, 1f);
                        LoadKeyframeToInputs(k2);
                        EvaluateAllTracks(_playbackTime);
                        e.Use();
                    }
                }
            }

            for (int i = 0; i < keys.Count; i++)
            {
                KeyframeData kf = keys[i];
                float kx = TimeToLaneX(kf.Time, graphCanvas.x, graphCanvas.width);
                if (kx < graphCanvas.x || kx > graphCanvas.xMax) continue;

                float ky = (i == 0) ? (graphCanvas.y + padY + plotHeight) : (graphCanvas.y + padY);
                bool isSel = _selectedKeys.Contains(kf);
                DrawDiamond(new Vector2(kx, ky), 6f, isSel ? new Color(0.25f, 0.78f, 1f) : Color.white);

                Rect hit = new Rect(kx - 8f, ky - 8f, 16f, 16f);
                if (e.type == EventType.MouseDown && e.button == 0 && hit.Contains(e.mousePosition))
                {
                    if (e.shift)
                        ToggleKeyInSelection(_selectedSubTrack, kf);
                    else
                        SelectSingleKey(_selectedSubTrack, kf);

                    SetPlaybackTime(kf.Time);
                    e.Use();
                }
            }

            // Graph Editor 空白處點擊取消選取
            if (e.type == EventType.MouseDown && e.button == 0 && graphCanvas.Contains(e.mousePosition) && !e.shift)
            {
                ClearKeySelection();
                e.Use();
            }
        }

        private float EvaluateEasingFactor(KeyframeData k1, KeyframeData k2, float u)
        {
            if (k1.Easing == EasingMode.Hold) return 0f;
            if (k1.Easing == EasingMode.Linear) return u;
            if (k1.Easing == EasingMode.EaseInOut) return Mathf.SmoothStep(0f, 1f, u);

            float p0 = 0f;
            float p1 = k1.OutTangentValue;
            float p2 = k2.InTangentValue;
            float p3 = 1f;
            float oneMinusU = 1f - u;
            return (oneMinusU * oneMinusU * oneMinusU * p0) +
                   (3f * oneMinusU * oneMinusU * u * p1) +
                   (3f * oneMinusU * u * u * p2) +
                   (u * u * u * p3);
        }

        private void DrawLine(Vector2 p1, Vector2 p2, float width, Color color)
        {
            Vector2 delta = p2 - p1;
            float len = delta.magnitude;
            if (len < 0.5f) return;

            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            Color oldCol = GUI.color;
            Matrix4x4 oldMat = GUI.matrix;

            GUI.color = color;
            GUIUtility.RotateAroundPivot(angle, p1);
            GUI.DrawTexture(new Rect(p1.x, p1.y - width * 0.5f, len, width), _whiteTex);

            GUI.matrix = oldMat;
            GUI.color = oldCol;
        }

        private void DrawRootTrackRow(ObjectTrack root, Rect leftCell, Rect trackLane, bool dark)
        {
            bool isSelected = (_selectedRootTrack == root);
            Color bg = isSelected
                ? (dark ? new Color(0.24f, 0.30f, 0.40f) : new Color(0.75f, 0.83f, 0.94f))
                : (dark ? new Color(0.20f, 0.21f, 0.24f) : new Color(0.84f, 0.85f, 0.88f));

            DrawRect(leftCell, bg);
            DrawRect(trackLane, dark ? new Color(0.15f, 0.16f, 0.18f) : new Color(0.80f, 0.81f, 0.84f));

            if (GUI.Button(new Rect(leftCell.x + 4, leftCell.y + 3, 22, 22), root.IsExpanded ? "▼" : "▶", GUI.skin.label))
                root.IsExpanded = !root.IsExpanded;

            string tag = root.IsCharacter ? "[角色] " : "[物件] ";
            if (GUI.Button(new Rect(leftCell.x + 26, leftCell.y + 3, leftCell.width - 115, 22), tag + root.Name, GUI.skin.label))
                _selectedRootTrack = root;

            if (root.IsCharacter)
            {
                if (GUI.Button(new Rect(leftCell.xMax - 86, leftCell.y + 3, 28, 22), new GUIContent("↻", "重新掃描角色已勾選啟用的 FK / IK 骨骼")))
                {
                    RefreshCharacterSubTracks(root);
                    root.IsExpanded = true;
                }
            }

            if (GUI.Button(new Rect(leftCell.xMax - 56, leftCell.y + 3, 26, 22), new GUIContent("◆+", "為所有啟用子軌道打關鍵影格")))
            {
                foreach (var sub in root.SubTracks)
                {
                    if (sub.StopwatchEnabled)
                        RecordSubTrackKeyframe(sub, _playbackTime);
                }
            }

            if (GUI.Button(new Rect(leftCell.xMax - 28, leftCell.y + 3, 24, 22), "×"))
            {
                _tracks.Remove(root.ObjectId);
                if (_selectedRootTrack == root)
                {
                    _selectedRootTrack = null;
                    _selectedSubTrack = null;
                    ClearKeySelection();
                }
                return;
            }

            foreach (var sub in root.SubTracks)
            {
                foreach (var kf in sub.Keyframes)
                {
                    float kx = TimeToLaneX(kf.Time, trackLane.x, trackLane.width);
                    if (kx < trackLane.x || kx > trackLane.xMax) continue;
                    float ky = trackLane.y + trackLane.height * 0.5f;
                    DrawRect(new Rect(kx - 2f, ky - 2f, 4f, 4f), new Color(0.55f, 0.78f, 1f, 0.7f));
                }
            }
        }

        private void DrawSubTrackRow(ObjectTrack root, SubTrack sub, Rect leftCell, Rect trackLane, float laneWidth, bool dark, bool evenRow, List<RenderedDiamondInfo> renderedDiamonds)
        {
            bool isSelected = (_selectedSubTrack == sub);
            Color rowBg = isSelected
                ? (dark ? new Color(0.20f, 0.27f, 0.36f) : new Color(0.78f, 0.86f, 0.96f))
                : (evenRow ? (dark ? new Color(0.16f, 0.16f, 0.18f) : new Color(0.90f, 0.90f, 0.92f))
                           : (dark ? new Color(0.14f, 0.14f, 0.16f) : new Color(0.87f, 0.87f, 0.89f)));

            DrawRect(leftCell, rowBg);
            DrawRect(trackLane, dark ? new Color(0.11f, 0.11f, 0.13f) : new Color(0.84f, 0.84f, 0.86f));

            float indent = leftCell.x + 18f;

            Color prevBg = GUI.backgroundColor;
            if (sub.StopwatchEnabled) GUI.backgroundColor = new Color(0.22f, 0.65f, 1f);
            if (GUI.Button(new Rect(indent, leftCell.y + 3, 22, 21), "⏱"))
            {
                sub.StopwatchEnabled = !sub.StopwatchEnabled;
                if (sub.StopwatchEnabled && sub.Keyframes.Count == 0)
                    RecordSubTrackKeyframe(sub, _playbackTime);
            }
            GUI.backgroundColor = prevBg;

            bool hasKeyHere = sub.Keyframes.Any(k => Mathf.Abs(k.Time - _playbackTime) < 0.015f);
            if (hasKeyHere) GUI.backgroundColor = new Color(0.25f, 0.72f, 1f);
            if (GUI.Button(new Rect(indent + 24, leftCell.y + 3, 24, 21), "◆"))
            {
                sub.StopwatchEnabled = true;
                RecordSubTrackKeyframe(sub, _playbackTime);
            }
            GUI.backgroundColor = prevBg;

            Color typeColor = GetSubTrackBadgeColor(sub.Type, dark);
            Color prevContent = GUI.contentColor;
            GUI.contentColor = typeColor;
            if (GUI.Button(new Rect(indent + 52, leftCell.y + 3, leftCell.width - 96, 21), sub.DisplayName, GUI.skin.label))
            {
                _selectedRootTrack = root;
                _selectedSubTrack = sub;
            }
            GUI.contentColor = prevContent;

            if (sub.Type != SubTrackType.RootTransform)
            {
                if (GUI.Button(new Rect(leftCell.xMax - 24, leftCell.y + 3, 20, 21), "-"))
                {
                    root.SubTracks.Remove(sub);
                    if (_selectedSubTrack == sub)
                    {
                        _selectedSubTrack = null;
                        ClearKeySelection();
                    }
                    return;
                }
            }

            Event e = Event.current;
            foreach (var kf in sub.Keyframes)
            {
                float kx = TimeToLaneX(kf.Time, trackLane.x, trackLane.width);
                if (kx < trackLane.x - 6f || kx > trackLane.xMax + 6f) continue;

                float ky = trackLane.y + trackLane.height * 0.5f;
                Vector2 center = new Vector2(kx, ky);
                renderedDiamonds.Add(new RenderedDiamondInfo { Center = center, SubTrack = sub, Key = kf });

                Rect hitRect = new Rect(kx - 7f, ky - 7f, 14f, 14f);

                bool isKeySelected = _selectedKeys.Contains(kf);
                Color diamondColor = isKeySelected
                    ? new Color(0.25f, 0.78f, 1f, 1f)
                    : (kf.Easing == EasingMode.CustomBezier ? new Color(1f, 0.78f, 0.30f, 1f) : new Color(0.86f, 0.87f, 0.90f, 1f));

                DrawDiamond(center, 5.5f, diamondColor);

                if (e.type == EventType.MouseDown && e.button == 0 && hitRect.Contains(e.mousePosition))
                {
                    _selectedRootTrack = root;

                    if (e.shift)
                    {
                        // 按住 Shift：加選 / 減選關鍵影格
                        ToggleKeyInSelection(sub, kf);
                    }
                    else
                    {
                        // 若點選的關鍵影格不在現有多重選取中，改為單選它；若已在多重選取中，保留整批選取以準備拖曳
                        if (!_selectedKeys.Contains(kf))
                            SelectSingleKey(sub, kf);
                        else
                        {
                            _selectedSubTrack = sub;
                            _primarySelectedKey = kf;
                            LoadKeyframeToInputs(kf);
                        }
                    }

                    // 啟動整批拖曳或 Alt + 拖曳最外側時間伸縮（Time-Stretch）
                    if (_selectedKeys.Count > 0)
                    {
                        _isDraggingKeys = true;
                        _dragStartTime = LaneXToTime(e.mousePosition.x, LeftHeaderWidth, laneWidth);
                        _dragInitialTimes.Clear();
                        foreach (var selKf in _selectedKeys)
                            _dragInitialTimes[selKf] = selKf.Time;

                        float minT = _dragInitialTimes.Values.Min();
                        float maxT = _dragInitialTimes.Values.Max();
                        float span = maxT - minT;

                        if (e.alt && _selectedKeys.Count >= 2 && span > 0.001f)
                        {
                            // 判斷點擊的是較靠近最左側或最右側的關鍵影格，將另一端設為錨點
                            bool clickedLeftSide = Mathf.Abs(kf.Time - minT) <= Mathf.Abs(kf.Time - maxT);
                            _isTimeStretching = true;
                            _stretchAnchorTime = clickedLeftSide ? maxT : minT;
                            _stretchInitialSpan = span;
                        }
                        else
                        {
                            _isTimeStretching = false;
                        }
                    }

                    SetPlaybackTime(kf.Time);
                    e.Use();
                }
            }
        }

        private Color GetSubTrackBadgeColor(SubTrackType type, bool dark)
        {
            switch (type)
            {
                case SubTrackType.IKNode:
                    return dark ? new Color(0.98f, 0.65f, 0.35f) : new Color(0.75f, 0.38f, 0.05f);
                case SubTrackType.FKBone:
                    return dark ? new Color(0.45f, 0.88f, 0.62f) : new Color(0.10f, 0.55f, 0.28f);
                case SubTrackType.FaceExpression:
                    return dark ? new Color(0.95f, 0.55f, 0.82f) : new Color(0.72f, 0.18f, 0.52f);
                default:
                    return dark ? new Color(0.88f, 0.90f, 0.94f) : new Color(0.15f, 0.15f, 0.18f);
            }
        }

        private void DrawTimelineRuler(Rect rulerRect, float usableWidth, bool dark)
        {
            DrawRect(rulerRect, dark ? new Color(0.11f, 0.11f, 0.13f) : new Color(0.80f, 0.80f, 0.83f));

            int stepCount = 10;
            for (int i = 0; i <= stepCount; i++)
            {
                float screenRatio = (float)i / stepCount;
                float x = rulerRect.x + screenRatio * usableWidth;
                float sec = (_viewStartRatio + screenRatio * VisibleWindowRatio) * _duration;

                DrawRect(new Rect(x, rulerRect.y + 16, 1, 12), dark ? Color.gray : Color.black);
                GUI.Label(new Rect(x + 2, rulerRect.y + 2, 46, 16), $"{sec:F2}s");
            }

            Event e = Event.current;
            Rect hitRuler = new Rect(rulerRect.x, rulerRect.y, usableWidth, rulerRect.height);
            if (e.type == EventType.MouseDown && e.button == 0 && hitRuler.Contains(e.mousePosition))
            {
                _isScrubbingRuler = true;
                _isPlaying = false;
                SetPlaybackTime(LaneXToTime(e.mousePosition.x, rulerRect.x, usableWidth));
                e.Use();
            }
            else if (_isScrubbingRuler && (e.type == EventType.MouseDrag || e.type == EventType.MouseDown))
            {
                SetPlaybackTime(LaneXToTime(e.mousePosition.x, rulerRect.x, usableWidth));
                e.Use();
            }
        }

        private void SetPlaybackTime(float newTime)
        {
            _playbackTime = Mathf.Clamp(newTime, 0f, _duration);
            _timeInputStr = _playbackTime.ToString("F2");
            EvaluateAllTracks(_playbackTime);
        }

        private void DrawNumericInspector(Rect rect, bool dark)
        {
            GUI.Box(rect, "");
            GUILayout.BeginArea(new Rect(rect.x + 6, rect.y + 4, rect.width - 12, rect.height - 8));
            _inspectorScrollPos = GUILayout.BeginScrollView(_inspectorScrollPos);

            GUILayout.Label($"Inspector ({_selectedKeys.Count} selected)", GUI.skin.box);

            if (_primarySelectedKey != null && _selectedSubTrack != null)
            {
                GUILayout.Label($"Track: {_selectedSubTrack.DisplayName}");
                GUILayout.Label($"Time: {_primarySelectedKey.Time:F3} s");
                GUILayout.Space(4);

                if (_selectedSubTrack.Type == SubTrackType.RootTransform || _selectedSubTrack.Type == SubTrackType.IKNode)
                {
                    DrawVec3Row("Pos", ref _posXStr, ref _posYStr, ref _posZStr, "Pos");
                    DrawVec3Row("Rot", ref _rotXStr, ref _rotYStr, ref _rotZStr, "Rot");
                    if (_selectedSubTrack.Type == SubTrackType.RootTransform)
                        DrawVec3Row("Scl", ref _scaleXStr, ref _scaleYStr, ref _scaleZStr, "Scl");
                }
                else if (_selectedSubTrack.Type == SubTrackType.FKBone)
                {
                    DrawVec3Row("Rot", ref _rotXStr, ref _rotYStr, ref _rotZStr, "Rot");
                }
                else if (_selectedSubTrack.Type == SubTrackType.FaceExpression)
                {
                    DrawSingleField("眉毛樣式 (ID):", ref _faceEyebrowStr, "Brow");
                    DrawSingleField("眼睛樣式 (ID):", ref _faceEyePtnStr, "EyePtn");
                    DrawSingleField("嘴巴樣式 (ID):", ref _faceMouthPtnStr, "MouthPtn");
                    DrawSingleField("眼睛開合 (0~1):", ref _faceEyeOpenStr, "EyeOpen");
                    DrawSingleField("臉紅濃度 (0~1):", ref _faceBlushStr, "Blush");
                }

                GUILayout.Space(4);
                EasingMode prevEase = _primarySelectedKey.Easing;
                EasingMode newEase = (EasingMode)GUILayout.SelectionGrid((int)prevEase, new[] { "Linear", "EaseIO", "Hold", "Bezier" }, 4);
                if (newEase != prevEase)
                {
                    foreach (var selKf in _selectedKeys)
                        selKf.Easing = newEase;
                    EvaluateAllTracks(_playbackTime);
                }

                if (_primarySelectedKey.Easing == EasingMode.CustomBezier)
                {
                    GUILayout.Space(4);
                    GUILayout.Label("Bezier Tangent Handles (0~1):");
                    DrawSingleField("Out Inf / Val:", ref _outInfStr, "OutInf");
                    DrawSingleField("Out Weight:", ref _outValStr, "OutVal");
                    DrawSingleField("In Inf / Val:", ref _inInfStr, "InInf");
                    DrawSingleField("In Weight:", ref _inValStr, "InVal");
                }

                GUILayout.Space(6);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("套用數值"))
                {
                    ApplyInputsToKeyframe(_primarySelectedKey);
                    EvaluateAllTracks(_playbackTime);
                }
                if (GUILayout.Button($"刪除 ({_selectedKeys.Count})", GUILayout.Width(75)))
                {
                    foreach (var root in _tracks.Values)
                        foreach (var sub in root.SubTracks)
                            sub.Keyframes.RemoveAll(k => _selectedKeys.Contains(k));
                    ClearKeySelection();
                }
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label("AE 操作快捷鍵一覽：\n• 軌道空白處拖曳：框選多顆關鍵幀\n• 軌道空白處單擊：取消目前選取\n• Shift + 點擊/框選：加選或減選\n• Alt + 拖曳最外側幀：時間伸縮 (Time-Stretch)\n• F9：套用柔和漸變 (Easy Ease)\n• Ctrl+C / V：跨軌道整批複製貼上\n• 拖曳視窗右下角：自由縮放視窗大小");
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawVec3Row(string label, ref string x, ref string y, ref string z, string ctrlPrefix)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(28));
            GUI.SetNextControlName($"NumInput_{ctrlPrefix}X");
            x = GUILayout.TextField(x, GUILayout.Width(66));
            GUI.SetNextControlName($"NumInput_{ctrlPrefix}Y");
            y = GUILayout.TextField(y, GUILayout.Width(66));
            GUI.SetNextControlName($"NumInput_{ctrlPrefix}Z");
            z = GUILayout.TextField(z, GUILayout.Width(66));
            GUILayout.EndHorizontal();
        }

        private void DrawSingleField(string label, ref string val, string ctrlName)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(115));
            GUI.SetNextControlName($"NumInput_{ctrlName}");
            val = GUILayout.TextField(val, GUILayout.Width(95));
            GUILayout.EndHorizontal();
        }

        private void DrawRect(Rect r, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, _whiteTex);
            GUI.color = old;
        }

        private void DrawDiamond(Vector2 center, float radius, Color c)
        {
            Color old = GUI.color;
            GUI.color = c;
            Matrix4x4 oldMatrix = GUI.matrix;
            GUIUtility.RotateAroundPivot(45f, center);
            GUI.DrawTexture(new Rect(center.x - radius, center.y - radius, radius * 2f, radius * 2f), _whiteTex);
            GUI.matrix = oldMatrix;
            GUI.color = old;
        }

        private void AddSelectedStudioObjects()
        {
            foreach (ObjectCtrlInfo oci in StudioAPI.GetSelectedObjects())
            {
                if (oci?.guideObject?.transformTarget == null) continue;
                int id = oci.objectInfo.dicKey;
                if (_tracks.ContainsKey(id)) continue;

                OCIChar chara = oci as OCIChar;
                var rootTrack = new ObjectTrack
                {
                    ObjectId = id,
                    Name = oci.treeNodeObject != null ? oci.treeNodeObject.textName : $"Object_{id}",
                    RootTransform = oci.guideObject.transformTarget,
                    IsCharacter = (chara != null),
                    CharaCtrl = chara,
                    IsExpanded = (chara != null)
                };

                var rootSub = new SubTrack
                {
                    TrackKey = "ROOT",
                    DisplayName = "Root Transform",
                    Type = SubTrackType.RootTransform,
                    TargetTransform = oci.guideObject.transformTarget,
                    StopwatchEnabled = true
                };
                RecordSubTrackKeyframe(rootSub, _playbackTime);
                rootTrack.SubTracks.Add(rootSub);

                if (chara != null)
                {
                    var faceSub = new SubTrack
                    {
                        TrackKey = "FACE",
                        DisplayName = "[Face] 表情 / 眼神 / 臉紅",
                        Type = SubTrackType.FaceExpression,
                        CharaCtrl = chara,
                        StopwatchEnabled = false
                    };
                    rootTrack.SubTracks.Add(faceSub);

                    RefreshCharacterSubTracks(rootTrack);
                }

                _tracks[id] = rootTrack;
                _selectedRootTrack = rootTrack;
                _selectedSubTrack = rootSub;
            }
        }

        private void RefreshCharacterSubTracks(ObjectTrack rootTrack)
        {
            if (!rootTrack.IsCharacter || rootTrack.CharaCtrl == null) return;
            OCIChar chara = rootTrack.CharaCtrl;

            if (chara.listIKTarget != null)
            {
                for (int i = 0; i < chara.listIKTarget.Count; i++)
                {
                    var ik = chara.listIKTarget[i];
                    if (ik?.guideObject?.transformTarget == null || !ik.active) continue;

                    string key = $"IK_{i}";
                    if (rootTrack.SubTracks.Any(s => s.TrackKey == key)) continue;

                    string boneName = ik.guideObject.transformTarget.name;
                    rootTrack.SubTracks.Add(new SubTrack
                    {
                        TrackKey = key,
                        DisplayName = $"[IK] {CleanBoneName(boneName)}",
                        Type = SubTrackType.IKNode,
                        TargetTransform = ik.guideObject.transformTarget,
                        StopwatchEnabled = true
                    });
                }
            }

            if (chara.listBones != null)
            {
                for (int i = 0; i < chara.listBones.Count; i++)
                {
                    var fk = chara.listBones[i];
                    if (fk?.guideObject?.transformTarget == null || !fk.active) continue;

                    string key = $"FK_{i}";
                    if (rootTrack.SubTracks.Any(s => s.TrackKey == key)) continue;

                    string boneName = fk.guideObject.transformTarget.name;
                    rootTrack.SubTracks.Add(new SubTrack
                    {
                        TrackKey = key,
                        DisplayName = $"[FK] {CleanBoneName(boneName)}",
                        Type = SubTrackType.FKBone,
                        TargetTransform = fk.guideObject.transformTarget,
                        StopwatchEnabled = true
                    });
                }
            }
        }

        private string CleanBoneName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Bone";
            return raw.Replace("cf_j_", "").Replace("cf_t_", "").Replace("k_f_", "").Replace("_00", "");
        }

        private void RecordSubTrackKeyframe(SubTrack sub, float time)
        {
            if (!sub.IsValid()) return;

            var existing = sub.Keyframes.FirstOrDefault(k => Mathf.Abs(k.Time - time) < 0.015f);
            if (existing == null)
            {
                existing = new KeyframeData { Time = time };
                sub.Keyframes.Add(existing);
                sub.Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
            }

            if (sub.Type == SubTrackType.RootTransform || sub.Type == SubTrackType.IKNode)
            {
                existing.Position = sub.TargetTransform.localPosition;
                existing.Rotation = sub.TargetTransform.localEulerAngles;
                existing.Scale = sub.TargetTransform.localScale;
            }
            else if (sub.Type == SubTrackType.FKBone)
            {
                existing.Rotation = sub.TargetTransform.localEulerAngles;
            }
            else if (sub.Type == SubTrackType.FaceExpression && sub.CharaCtrl?.charInfo != null)
            {
                var st = sub.CharaCtrl.charInfo.fileStatus;
                existing.EyebrowPtn = st.eyebrowPtn;
                existing.EyePtn = st.eyesPtn;
                existing.MouthPtn = st.mouthPtn;
                existing.EyeOpenMax = st.eyesOpenMax;
                existing.BlushRate = st.hohoAkaRate;
            }

            SelectSingleKey(sub, existing);
        }

        private void LoadKeyframeToInputs(KeyframeData kf)
        {
            _posXStr = kf.Position.x.ToString("F3");
            _posYStr = kf.Position.y.ToString("F3");
            _posZStr = kf.Position.z.ToString("F3");

            _rotXStr = kf.Rotation.x.ToString("F3");
            _rotYStr = kf.Rotation.y.ToString("F3");
            _rotZStr = kf.Rotation.z.ToString("F3");

            _scaleXStr = kf.Scale.x.ToString("F3");
            _scaleYStr = kf.Scale.y.ToString("F3");
            _scaleZStr = kf.Scale.z.ToString("F3");

            _faceEyebrowStr = kf.EyebrowPtn.ToString();
            _faceEyePtnStr = kf.EyePtn.ToString();
            _faceMouthPtnStr = kf.MouthPtn.ToString();
            _faceEyeOpenStr = kf.EyeOpenMax.ToString("F2");
            _faceBlushStr = kf.BlushRate.ToString("F2");

            _outInfStr = kf.OutInfluence.ToString("F2");
            _outValStr = kf.OutTangentValue.ToString("F2");
            _inInfStr = kf.InInfluence.ToString("F2");
            _inValStr = kf.InTangentValue.ToString("F2");
        }

        private void ApplyInputsToKeyframe(KeyframeData kf)
        {
            if (float.TryParse(_posXStr, out float px)) kf.Position.x = px;
            if (float.TryParse(_posYStr, out float py)) kf.Position.y = py;
            if (float.TryParse(_posZStr, out float pz)) kf.Position.z = pz;

            if (float.TryParse(_rotXStr, out float rx)) kf.Rotation.x = rx;
            if (float.TryParse(_rotYStr, out float ry)) kf.Rotation.y = ry;
            if (float.TryParse(_rotZStr, out float rz)) kf.Rotation.z = rz;

            if (float.TryParse(_scaleXStr, out float sx)) kf.Scale.x = sx;
            if (float.TryParse(_scaleYStr, out float sy)) kf.Scale.y = sy;
            if (float.TryParse(_scaleZStr, out float sz)) kf.Scale.z = sz;

            if (int.TryParse(_faceEyebrowStr, out int eb)) kf.EyebrowPtn = eb;
            if (int.TryParse(_faceEyePtnStr, out int ep)) kf.EyePtn = ep;
            if (int.TryParse(_faceMouthPtnStr, out int mp)) kf.MouthPtn = mp;
            if (float.TryParse(_faceEyeOpenStr, out float eo)) kf.EyeOpenMax = Mathf.Clamp01(eo);
            if (float.TryParse(_faceBlushStr, out float bl)) kf.BlushRate = Mathf.Clamp01(bl);

            if (float.TryParse(_outInfStr, out float oi)) kf.OutInfluence = Mathf.Clamp(oi, 0.05f, 0.95f);
            if (float.TryParse(_outValStr, out float ov)) kf.OutTangentValue = Mathf.Clamp(ov, 0f, 1f);
            if (float.TryParse(_inInfStr, out float ii)) kf.InInfluence = Mathf.Clamp(ii, 0.05f, 0.95f);
            if (float.TryParse(_inValStr, out float iv)) kf.InTangentValue = Mathf.Clamp(iv, 0f, 1f);
        }

        private void EvaluateAllTracks(float time)
        {
            foreach (var root in _tracks.Values)
            {
                foreach (var sub in root.SubTracks)
                {
                    if (!sub.IsValid() || sub.Keyframes.Count == 0) continue;
                    KeyframeData eval = EvaluateSubTrackValues(sub, time);
                    if (eval == null) continue;

                    if (sub.Type == SubTrackType.RootTransform)
                    {
                        sub.TargetTransform.localPosition = eval.Position;
                        sub.TargetTransform.localEulerAngles = eval.Rotation;
                        sub.TargetTransform.localScale = eval.Scale;
                    }
                    else if (sub.Type == SubTrackType.IKNode)
                    {
                        sub.TargetTransform.localPosition = eval.Position;
                        sub.TargetTransform.localEulerAngles = eval.Rotation;
                    }
                    else if (sub.Type == SubTrackType.FKBone)
                    {
                        sub.TargetTransform.localEulerAngles = eval.Rotation;
                    }
                    else if (sub.Type == SubTrackType.FaceExpression && sub.CharaCtrl?.charInfo != null)
                    {
                        var ci = sub.CharaCtrl.charInfo;
                        ci.ChangeEyebrowPtn(eval.EyebrowPtn);
                        ci.ChangeEyesPtn(eval.EyePtn);
                        ci.ChangeMouthPtn(eval.MouthPtn);
                        ci.ChangeEyesOpenMax(eval.EyeOpenMax);
                        ci.ChangeHohoAkaRate(eval.BlushRate);
                    }
                }
            }
        }

        private KeyframeData EvaluateSubTrackValues(SubTrack sub, float time)
        {
            if (sub.Keyframes.Count == 0) return null;
            if (time <= sub.Keyframes[0].Time) return sub.Keyframes[0];

            int lastIdx = sub.Keyframes.Count - 1;
            if (time >= sub.Keyframes[lastIdx].Time) return sub.Keyframes[lastIdx];

            for (int i = 0; i < lastIdx; i++)
            {
                KeyframeData k1 = sub.Keyframes[i];
                KeyframeData k2 = sub.Keyframes[i + 1];
                if (time >= k1.Time && time <= k2.Time)
                {
                    if (k1.Easing == EasingMode.Hold) return k1;

                    float u = Mathf.InverseLerp(k1.Time, k2.Time, time);
                    float t = EvaluateEasingFactor(k1, k2, u);

                    return new KeyframeData
                    {
                        Time = time,
                        Position = Vector3.Lerp(k1.Position, k2.Position, t),
                        Rotation = Quaternion.Slerp(Quaternion.Euler(k1.Rotation), Quaternion.Euler(k2.Rotation), t).eulerAngles,
                        Scale = Vector3.Lerp(k1.Scale, k2.Scale, t),
                        EyebrowPtn = k1.EyebrowPtn,
                        EyePtn = k1.EyePtn,
                        MouthPtn = k1.MouthPtn,
                        EyeOpenMax = Mathf.Lerp(k1.EyeOpenMax, k2.EyeOpenMax, t),
                        BlushRate = Mathf.Lerp(k1.BlushRate, k2.BlushRate, t)
                    };
                }
            }
            return sub.Keyframes[lastIdx];
        }

        public PluginData SaveSceneData()
        {
            if (_tracks.Count == 0) return null;

            var doc = new XmlDocument();
            XmlElement root = doc.CreateElement("KeyframeTool");
            root.SetAttribute("duration", _duration.ToString("R"));
            doc.AppendChild(root);

            foreach (var track in _tracks.Values)
            {
                XmlElement trackNode = doc.CreateElement("Track");
                trackNode.SetAttribute("id", track.ObjectId.ToString());
                trackNode.SetAttribute("name", track.Name);
                trackNode.SetAttribute("isChara", track.IsCharacter ? "1" : "0");

                foreach (var sub in track.SubTracks)
                {
                    if (sub.Keyframes.Count == 0) continue;
                    XmlElement subNode = doc.CreateElement("SubTrack");
                    subNode.SetAttribute("key", sub.TrackKey);
                    subNode.SetAttribute("name", sub.DisplayName);
                    subNode.SetAttribute("type", ((int)sub.Type).ToString());

                    foreach (var kf in sub.Keyframes)
                    {
                        XmlElement keyNode = doc.CreateElement("Key");
                        keyNode.SetAttribute("t", kf.Time.ToString("R"));
                        keyNode.SetAttribute("px", kf.Position.x.ToString("R"));
                        keyNode.SetAttribute("py", kf.Position.y.ToString("R"));
                        keyNode.SetAttribute("pz", kf.Position.z.ToString("R"));
                        keyNode.SetAttribute("rx", kf.Rotation.x.ToString("R"));
                        keyNode.SetAttribute("ry", kf.Rotation.y.ToString("R"));
                        keyNode.SetAttribute("rz", kf.Rotation.z.ToString("R"));
                        keyNode.SetAttribute("sx", kf.Scale.x.ToString("R"));
                        keyNode.SetAttribute("sy", kf.Scale.y.ToString("R"));
                        keyNode.SetAttribute("sz", kf.Scale.z.ToString("R"));
                        keyNode.SetAttribute("eb", kf.EyebrowPtn.ToString());
                        keyNode.SetAttribute("ep", kf.EyePtn.ToString());
                        keyNode.SetAttribute("mp", kf.MouthPtn.ToString());
                        keyNode.SetAttribute("eo", kf.EyeOpenMax.ToString("R"));
                        keyNode.SetAttribute("bl", kf.BlushRate.ToString("R"));
                        keyNode.SetAttribute("ease", ((int)kf.Easing).ToString());
                        keyNode.SetAttribute("oi", kf.OutInfluence.ToString("R"));
                        keyNode.SetAttribute("ov", kf.OutTangentValue.ToString("R"));
                        keyNode.SetAttribute("ii", kf.InInfluence.ToString("R"));
                        keyNode.SetAttribute("iv", kf.InTangentValue.ToString("R"));
                        subNode.AppendChild(keyNode);
                    }
                    trackNode.AppendChild(subNode);
                }
                root.AppendChild(trackNode);
            }

            var data = new PluginData();
            data.data["xml"] = doc.OuterXml;
            return data;
        }

        public void LoadSceneData(PluginData data, ReadOnlyDictionary<int, ObjectCtrlInfo> loadedObjects)
        {
            _tracks.Clear();
            _selectedRootTrack = null;
            _selectedSubTrack = null;
            ClearKeySelection();

            if (data == null || !data.data.TryGetValue("xml", out object xmlObj) || !(xmlObj is string xmlStr))
                return;

            var doc = new XmlDocument();
            doc.LoadXml(xmlStr);
            XmlElement root = doc.DocumentElement;
            if (root == null) return;

            if (float.TryParse(root.GetAttribute("duration"), out float dur))
            {
                _duration = dur;
                _durationInputStr = _duration.ToString("F2");
            }

            foreach (XmlElement trackNode in root.GetElementsByTagName("Track"))
            {
                if (!int.TryParse(trackNode.GetAttribute("id"), out int origId)) continue;
                if (!loadedObjects.TryGetValue(origId, out ObjectCtrlInfo oci) || oci?.guideObject?.transformTarget == null)
                    continue;

                OCIChar chara = oci as OCIChar;
                var rootTrack = new ObjectTrack
                {
                    ObjectId = oci.objectInfo.dicKey,
                    Name = trackNode.GetAttribute("name"),
                    RootTransform = oci.guideObject.transformTarget,
                    IsCharacter = (chara != null),
                    CharaCtrl = chara,
                    IsExpanded = true
                };

                foreach (XmlElement subNode in trackNode.GetElementsByTagName("SubTrack"))
                {
                    string key = subNode.GetAttribute("key");
                    SubTrackType type = (SubTrackType)int.Parse(subNode.GetAttribute("type"));
                    Transform targetTf = ResolveSubTrackTransform(oci, chara, key, type);
                    if (type != SubTrackType.FaceExpression && targetTf == null) continue;

                    var sub = new SubTrack
                    {
                        TrackKey = key,
                        DisplayName = subNode.GetAttribute("name"),
                        Type = type,
                        TargetTransform = targetTf,
                        CharaCtrl = chara,
                        StopwatchEnabled = true
                    };

                    foreach (XmlElement keyNode in subNode.GetElementsByTagName("Key"))
                    {
                        var kf = new KeyframeData
                        {
                            Time = float.Parse(keyNode.GetAttribute("t")),
                            Position = new Vector3(
                                float.Parse(keyNode.GetAttribute("px")),
                                float.Parse(keyNode.GetAttribute("py")),
                                float.Parse(keyNode.GetAttribute("pz"))),
                            Rotation = new Vector3(
                                float.Parse(keyNode.GetAttribute("rx")),
                                float.Parse(keyNode.GetAttribute("ry")),
                                float.Parse(keyNode.GetAttribute("rz"))),
                            Scale = new Vector3(
                                float.Parse(keyNode.GetAttribute("sx")),
                                float.Parse(keyNode.GetAttribute("sy")),
                                float.Parse(keyNode.GetAttribute("sz"))),
                            EyebrowPtn = int.Parse(keyNode.GetAttribute("eb")),
                            EyePtn = int.Parse(keyNode.GetAttribute("ep")),
                            MouthPtn = int.Parse(keyNode.GetAttribute("mp")),
                            EyeOpenMax = float.Parse(keyNode.GetAttribute("eo")),
                            BlushRate = float.Parse(keyNode.GetAttribute("bl")),
                            Easing = (EasingMode)int.Parse(keyNode.GetAttribute("ease"))
                        };
                        if (float.TryParse(keyNode.GetAttribute("oi"), out float oi)) kf.OutInfluence = oi;
                        if (float.TryParse(keyNode.GetAttribute("ov"), out float ov)) kf.OutTangentValue = ov;
                        if (float.TryParse(keyNode.GetAttribute("ii"), out float ii)) kf.InInfluence = ii;
                        if (float.TryParse(keyNode.GetAttribute("iv"), out float iv)) kf.InTangentValue = iv;

                        sub.Keyframes.Add(kf);
                    }
                    sub.Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
                    rootTrack.SubTracks.Add(sub);
                }

                _tracks[rootTrack.ObjectId] = rootTrack;
            }
        }

        private Transform ResolveSubTrackTransform(ObjectCtrlInfo oci, OCIChar chara, string key, SubTrackType type)
        {
            if (type == SubTrackType.RootTransform)
                return oci.guideObject.transformTarget;

            if (chara == null) return null;

            if (type == SubTrackType.IKNode && key.StartsWith("IK_") && int.TryParse(key.Substring(3), out int ikIdx))
            {
                if (chara.listIKTarget != null && ikIdx >= 0 && ikIdx < chara.listIKTarget.Count)
                    return chara.listIKTarget[ikIdx]?.guideObject?.transformTarget;
            }
            else if (type == SubTrackType.FKBone && key.StartsWith("FK_") && int.TryParse(key.Substring(3), out int fkIdx))
            {
                if (chara.listBones != null && fkIdx >= 0 && fkIdx < chara.listBones.Count)
                    return chara.listBones[fkIdx]?.guideObject?.transformTarget;
            }
            return null;
        }
    }

    public class RenderedDiamondInfo
    {
        public Vector2 Center;
        public SubTrack SubTrack;
        public KeyframeData Key;
    }

    public class ClipboardKeyEntry
    {
        public int RootObjectId;
        public string SubTrackKey;
        public float RelativeTimeOffset;
        public KeyframeData Data;
    }

    public class KeyframeToolSceneController : SceneCustomFunctionController
    {
        protected override void OnSceneSave()
        {
            PluginData data = KeyframeTool.Instance?.SaveSceneData();
            SetExtendedData(data);
        }

        protected override void OnSceneLoad(SceneOperationKind operation, ReadOnlyDictionary<int, ObjectCtrlInfo> loadedItems)
        {
            if (operation == SceneOperationKind.Clear)
            {
                KeyframeTool.Instance?.LoadSceneData(null, loadedItems);
                return;
            }
            PluginData data = GetExtendedData();
            KeyframeTool.Instance?.LoadSceneData(data, loadedItems);
        }
    }

    public class ObjectTrack
    {
        public int ObjectId;
        public string Name;
        public Transform RootTransform;
        public bool IsCharacter;
        public OCIChar CharaCtrl;
        public bool IsExpanded = true;
        public List<SubTrack> SubTracks = new List<SubTrack>();
    }

    public enum SubTrackType
    {
        RootTransform = 0,
        IKNode = 1,
        FKBone = 2,
        FaceExpression = 3
    }

    public class SubTrack
    {
        public string TrackKey;
        public string DisplayName;
        public SubTrackType Type;
        public Transform TargetTransform;
        public OCIChar CharaCtrl;
        public bool StopwatchEnabled;
        public List<KeyframeData> Keyframes = new List<KeyframeData>();

        public bool IsValid()
        {
            if (Type == SubTrackType.FaceExpression)
                return CharaCtrl != null && CharaCtrl.charInfo != null;
            return TargetTransform != null;
        }
    }

    public class KeyframeData
    {
        public float Time;
        public Vector3 Position;
        public Vector3 Rotation;
        public Vector3 Scale = Vector3.one;

        public int EyebrowPtn;
        public int EyePtn;
        public int MouthPtn;
        public float EyeOpenMax = 1f;
        public float BlushRate = 0f;

        public EasingMode Easing = EasingMode.Linear;

        public float OutInfluence = 0.33f;
        public float OutTangentValue = 0.0f;
        public float InInfluence = 0.33f;
        public float InTangentValue = 1.0f;

        public KeyframeData Clone()
        {
            return new KeyframeData
            {
                Time = Time,
                Position = Position,
                Rotation = Rotation,
                Scale = Scale,
                EyebrowPtn = EyebrowPtn,
                EyePtn = EyePtn,
                MouthPtn = MouthPtn,
                EyeOpenMax = EyeOpenMax,
                BlushRate = BlushRate,
                Easing = Easing,
                OutInfluence = OutInfluence,
                OutTangentValue = OutTangentValue,
                InInfluence = InInfluence,
                InTangentValue = InTangentValue
            };
        }
    }

    public enum EasingMode
    {
        Linear = 0,
        EaseInOut = 1,
        Hold = 2,
        CustomBezier = 3
    }
}