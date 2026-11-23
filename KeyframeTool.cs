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
        public const string Version = "1.0.0";
        public const string ExtDataKey = "KeyframeToolData";

        public static KeyframeTool Instance { get; private set; }

        private ConfigEntry<KeyboardShortcut> _toggleWindowKey;
        private ConfigEntry<KeyboardShortcut> _playPauseKey;
        private ConfigEntry<bool> _darkMode;

        private bool _showWindow = false;
        private Rect _windowRect = new Rect(120, 120, 780, 460);
        private Vector2 _trackScrollPos = Vector2.zero;
        private Vector2 _inspectorScrollPos = Vector2.zero;

        private float _playbackTime = 0f;
        private float _duration = 10f;
        private bool _isPlaying = false;
        private bool _loop = true;

        private string _timeInputStr = "0.000";
        private string _durationInputStr = "10.000";
        private string _posXStr = "0", _posYStr = "0", _posZStr = "0";
        private string _rotXStr = "0", _rotYStr = "0", _rotZStr = "0";
        private string _scaleXStr = "1", _scaleYStr = "1", _scaleZStr = "1";

        private readonly Dictionary<int, ObjectTrack> _tracks = new Dictionary<int, ObjectTrack>();
        private int _selectedTrackId = -1;
        private int _selectedKeyframeIndex = -1;

        private GUIStyle _headerStyle;
        private GUIStyle _boxStyle;

        private void Awake()
        {
            Instance = this;

            _toggleWindowKey = Config.Bind("Hotkeys", "Toggle KeyframeTool Window", new KeyboardShortcut(KeyCode.K, KeyCode.LeftControl), "開啟或關閉 KeyframeTool 視窗");
            _playPauseKey = Config.Bind("Hotkeys", "Play / Pause", new KeyboardShortcut(KeyCode.Space, KeyCode.LeftControl), "播放或暫停 KeyframeTool 時間軸");
            _darkMode = Config.Bind("Appearance", "Dark Mode", true, "切換深色 / 淺色極簡介面主題");

            StudioSaveLoadApi.RegisterExtraBehaviour<KeyframeToolSceneController>(ExtDataKey);
        }

        private void Update()
        {
            if (!StudioAPI.InsideStudio) return;

            if (_toggleWindowKey.Value.IsDown())
                _showWindow = !_showWindow;

            if (_playPauseKey.Value.IsDown())
                _isPlaying = !_isPlaying;

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
                _timeInputStr = _playbackTime.ToString("F3");
                EvaluateAllTracks(_playbackTime);
            }
        }

        private void OnGUI()
        {
            if (!StudioAPI.InsideStudio) return;

            if (!_showWindow)
            {
                if (GUI.Button(new Rect(10, 10, 115, 24), "KeyframeTool"))
                    _showWindow = true;
                return;
            }

            InitStyles();

            Color prevBg = GUI.backgroundColor;
            Color prevContent = GUI.contentColor;

            GUI.backgroundColor = _darkMode.Value ? new Color(0.16f, 0.17f, 0.19f, 0.97f) : new Color(0.94f, 0.95f, 0.96f, 0.97f);
            GUI.contentColor = _darkMode.Value ? new Color(0.92f, 0.93f, 0.95f, 1f) : new Color(0.12f, 0.13f, 0.15f, 1f);

            _windowRect = GUILayout.Window(948721, _windowRect, DrawMainWindow, "KeyframeTool — Independent Timeline Workspace");

            GUI.backgroundColor = prevBg;
            GUI.contentColor = prevContent;
        }

        private void InitStyles()
        {
            if (_headerStyle == null)
            {
                _headerStyle = new GUIStyle(GUI.skin.label)
                {
                    fontStyle = FontStyle.Bold,
                    fontSize = 12
                };
            }
            if (_boxStyle == null)
            {
                _boxStyle = new GUIStyle(GUI.skin.box)
                {
                    padding = new RectOffset(8, 8, 6, 6)
                };
            }
        }

        private void DrawMainWindow(int id)
        {
            GUILayout.BeginHorizontal(_boxStyle);
            if (GUILayout.Button(_isPlaying ? "❚❚ Pause" : "▶ Play", GUILayout.Width(75)))
                _isPlaying = !_isPlaying;

            if (GUILayout.Button("■ Stop", GUILayout.Width(60)))
            {
                _isPlaying = false;
                _playbackTime = 0f;
                _timeInputStr = "0.000";
                EvaluateAllTracks(0f);
            }

            _loop = GUILayout.Toggle(_loop, "Loop", GUILayout.Width(52));

            GUILayout.Space(10);
            GUILayout.Label("Time (s):", GUILayout.Width(58));
            _timeInputStr = GUILayout.TextField(_timeInputStr, GUILayout.Width(68));
            if (GUILayout.Button("Set", GUILayout.Width(36)) && float.TryParse(_timeInputStr, out float parsedTime))
            {
                _playbackTime = Mathf.Clamp(parsedTime, 0f, _duration);
                _timeInputStr = _playbackTime.ToString("F3");
                EvaluateAllTracks(_playbackTime);
            }

            GUILayout.Space(8);
            GUILayout.Label("Duration (s):", GUILayout.Width(78));
            _durationInputStr = GUILayout.TextField(_durationInputStr, GUILayout.Width(68));
            if (GUILayout.Button("Set", GUILayout.Width(36)) && float.TryParse(_durationInputStr, out float parsedDur) && parsedDur > 0.01f)
            {
                _duration = parsedDur;
                _durationInputStr = _duration.ToString("F3");
            }

            GUILayout.FlexibleSpace();
            _darkMode.Value = GUILayout.Toggle(_darkMode.Value, _darkMode.Value ? "Dark" : "Light", GUILayout.Width(55));
            if (GUILayout.Button("✕", GUILayout.Width(26)))
                _showWindow = false;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();

            GUILayout.BeginVertical(_boxStyle, GUILayout.Width(450), GUILayout.ExpandHeight(true));
            GUILayout.BeginHorizontal();
            GUILayout.Label("Object Tracks", _headerStyle);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("+ Add Selected Object", GUILayout.Width(155)))
                AddSelectedStudioObjects();
            GUILayout.EndHorizontal();

            _trackScrollPos = GUILayout.BeginScrollView(_trackScrollPos);
            foreach (var kvp in _tracks.ToList())
            {
                int objId = kvp.Key;
                ObjectTrack track = kvp.Value;
                if (track.Target == null) continue;

                bool isSelectedTrack = (_selectedTrackId == objId);
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button((isSelectedTrack ? "▼ " : "▶ ") + track.Name, GUI.skin.label, GUILayout.Width(210)))
                {
                    _selectedTrackId = objId;
                    _selectedKeyframeIndex = -1;
                }

                if (GUILayout.Button("+ Key @ " + _playbackTime.ToString("F2") + "s", GUILayout.Width(115)))
                {
                    AddOrUpdateKeyframe(track, _playbackTime);
                }

                if (GUILayout.Button("Del", GUILayout.Width(42)))
                {
                    _tracks.Remove(objId);
                    if (_selectedTrackId == objId) _selectedTrackId = -1;
                    break;
                }
                GUILayout.EndHorizontal();

                if (track.Keyframes.Count > 0)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("Keys:", GUILayout.Width(38));
                    for (int i = 0; i < track.Keyframes.Count; i++)
                    {
                        var kf = track.Keyframes[i];
                        bool isKeySel = isSelectedTrack && (_selectedKeyframeIndex == i);
                        string label = isKeySel ? $"[{kf.Time:F2}s]" : $"{kf.Time:F2}s";
                        if (GUILayout.Button(label, GUILayout.Width(58)))
                        {
                            _selectedTrackId = objId;
                            _selectedKeyframeIndex = i;
                            _playbackTime = kf.Time;
                            _timeInputStr = _playbackTime.ToString("F3");
                            LoadKeyframeToInputs(kf);
                            EvaluateAllTracks(_playbackTime);
                        }
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndVertical();
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.BeginVertical(_boxStyle, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            GUILayout.Label("Keyframe Inspector (Numeric)", _headerStyle);
            _inspectorScrollPos = GUILayout.BeginScrollView(_inspectorScrollPos);

            if (_tracks.TryGetValue(_selectedTrackId, out ObjectTrack selTrack) &&
                _selectedKeyframeIndex >= 0 && _selectedKeyframeIndex < selTrack.Keyframes.Count)
            {
                KeyframeData kf = selTrack.Keyframes[_selectedKeyframeIndex];
                GUILayout.Label($"Target: {selTrack.Name}");
                GUILayout.Label($"Key Time: {kf.Time:F3} s");
                GUILayout.Space(6);

                DrawVector3Inputs("Position", ref _posXStr, ref _posYStr, ref _posZStr);
                DrawVector3Inputs("Rotation", ref _rotXStr, ref _rotYStr, ref _rotZStr);
                DrawVector3Inputs("Scale", ref _scaleXStr, ref _scaleYStr, ref _scaleZStr);

                GUILayout.Space(6);
                kf.Easing = (EasingMode)GUILayout.SelectionGrid((int)kf.Easing, new[] { "Linear", "EaseInOut", "Hold" }, 3);

                GUILayout.Space(8);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Apply Numeric Values"))
                {
                    ApplyInputsToKeyframe(kf);
                    EvaluateAllTracks(_playbackTime);
                }
                if (GUILayout.Button("Remove Key", GUILayout.Width(95)))
                {
                    selTrack.Keyframes.RemoveAt(_selectedKeyframeIndex);
                    _selectedKeyframeIndex = -1;
                }
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.Label("選擇左側軌道中的關鍵影格，即可在此進行精準數值微調與曲線設定。");
            }

            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();

            GUI.DragWindow();
        }

        private void DrawVector3Inputs(string label, ref string xStr, ref string yStr, ref string zStr)
        {
            GUILayout.Label(label);
            GUILayout.BeginHorizontal();
            GUILayout.Label("X", GUILayout.Width(14));
            xStr = GUILayout.TextField(xStr, GUILayout.Width(72));
            GUILayout.Label("Y", GUILayout.Width(14));
            yStr = GUILayout.TextField(yStr, GUILayout.Width(72));
            GUILayout.Label("Z", GUILayout.Width(14));
            zStr = GUILayout.TextField(zStr, GUILayout.Width(72));
            GUILayout.EndHorizontal();
        }

        private void AddSelectedStudioObjects()
        {
            foreach (ObjectCtrlInfo oci in StudioAPI.GetSelectedObjects())
            {
                if (oci?.guideObject?.transformTarget == null) continue;
                int id = oci.objectInfo.dicKey;
                if (!_tracks.ContainsKey(id))
                {
                    _tracks[id] = new ObjectTrack
                    {
                        ObjectId = id,
                        Name = oci.treeNodeObject != null ? oci.treeNodeObject.textName : $"Object_{id}",
                        Target = oci.guideObject.transformTarget
                    };
                    _selectedTrackId = id;
                }
            }
        }

        private void AddOrUpdateKeyframe(ObjectTrack track, float time)
        {
            if (track.Target == null) return;

            var existing = track.Keyframes.FirstOrDefault(k => Mathf.Abs(k.Time - time) < 0.001f);
            if (existing == null)
            {
                existing = new KeyframeData { Time = time };
                track.Keyframes.Add(existing);
                track.Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
            }

            existing.Position = track.Target.localPosition;
            existing.Rotation = track.Target.localEulerAngles;
            existing.Scale = track.Target.localScale;

            _selectedTrackId = track.ObjectId;
            _selectedKeyframeIndex = track.Keyframes.IndexOf(existing);
            LoadKeyframeToInputs(existing);
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
        }

        private void EvaluateAllTracks(float time)
        {
            foreach (var track in _tracks.Values)
            {
                if (track.Target == null || track.Keyframes.Count == 0) continue;

                if (time <= track.Keyframes[0].Time)
                {
                    ApplyTransform(track.Target, track.Keyframes[0]);
                    continue;
                }

                int lastIdx = track.Keyframes.Count - 1;
                if (time >= track.Keyframes[lastIdx].Time)
                {
                    ApplyTransform(track.Target, track.Keyframes[lastIdx]);
                    continue;
                }

                for (int i = 0; i < lastIdx; i++)
                {
                    KeyframeData k1 = track.Keyframes[i];
                    KeyframeData k2 = track.Keyframes[i + 1];
                    if (time >= k1.Time && time <= k2.Time)
                    {
                        if (k1.Easing == EasingMode.Hold)
                        {
                            ApplyTransform(track.Target, k1);
                        }
                        else
                        {
                            float t = Mathf.InverseLerp(k1.Time, k2.Time, time);
                            if (k1.Easing == EasingMode.EaseInOut)
                                t = Mathf.SmoothStep(0f, 1f, t);

                            track.Target.localPosition = Vector3.Lerp(k1.Position, k2.Position, t);
                            track.Target.localRotation = Quaternion.Slerp(Quaternion.Euler(k1.Rotation), Quaternion.Euler(k2.Rotation), t);
                            track.Target.localScale = Vector3.Lerp(k1.Scale, k2.Scale, t);
                        }
                        break;
                    }
                }
            }
        }

        private void ApplyTransform(Transform target, KeyframeData kf)
        {
            target.localPosition = kf.Position;
            target.localEulerAngles = kf.Rotation;
            target.localScale = kf.Scale;
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
                foreach (var kf in track.Keyframes)
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
                    keyNode.SetAttribute("ease", ((int)kf.Easing).ToString());
                    trackNode.AppendChild(keyNode);
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
            _selectedTrackId = -1;
            _selectedKeyframeIndex = -1;

            if (data == null || !data.data.TryGetValue("xml", out object xmlObj) || !(xmlObj is string xmlStr))
                return;

            var doc = new XmlDocument();
            doc.LoadXml(xmlStr);
            XmlElement root = doc.DocumentElement;
            if (root == null) return;

            if (float.TryParse(root.GetAttribute("duration"), out float dur))
            {
                _duration = dur;
                _durationInputStr = _duration.ToString("F3");
            }

            foreach (XmlElement trackNode in root.GetElementsByTagName("Track"))
            {
                if (!int.TryParse(trackNode.GetAttribute("id"), out int origId)) continue;
                if (!loadedObjects.TryGetValue(origId, out ObjectCtrlInfo oci) || oci?.guideObject?.transformTarget == null)
                    continue;

                var track = new ObjectTrack
                {
                    ObjectId = oci.objectInfo.dicKey,
                    Name = trackNode.GetAttribute("name"),
                    Target = oci.guideObject.transformTarget
                };

                foreach (XmlElement keyNode in trackNode.GetElementsByTagName("Key"))
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
                        Easing = (EasingMode)int.Parse(keyNode.GetAttribute("ease"))
                    };
                    track.Keyframes.Add(kf);
                }

                track.Keyframes.Sort((a, b) => a.Time.CompareTo(b.Time));
                _tracks[track.ObjectId] = track;
            }
        }
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
        public Transform Target;
        public List<KeyframeData> Keyframes = new List<KeyframeData>();
    }

    public class KeyframeData
    {
        public float Time;
        public Vector3 Position;
        public Vector3 Rotation;
        public Vector3 Scale = Vector3.one;
        public EasingMode Easing = EasingMode.Linear;
    }

    public enum EasingMode
    {
        Linear = 0,
        EaseInOut = 1,
        Hold = 2
    }
}
